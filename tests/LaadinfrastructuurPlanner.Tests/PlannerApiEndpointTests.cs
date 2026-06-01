using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LaadinfrastructuurPlanner.Models;
using LaadinfrastructuurPlanner.Services;

namespace LaadinfrastructuurPlanner.Tests;

public sealed class PlannerApiEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "route-analysis-api-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerApiEndpointsReturnExpectedJson()
    {
        var cacheDir = Path.Combine(_root, ".cache");
        TestParquetData.WriteAll(cacheDir);
        var zeZonesPath = Path.Combine(cacheDir, "zez_pc6.csv");
        File.WriteAllText(
            zeZonesPath,
            "pc6,ze_zone,ze_startdatum\n1234AB,Test ZE-zone,2025-01-01\n");

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<RouteAnalysisOptions>();
                    services.AddSingleton(new RouteAnalysisOptions
                    {
                        RepoRoot = _root,
                        CacheDir = cacheDir,
                        UploadedDatasetDir = Path.Combine(cacheDir, "uploaded-dataset", "active"),
                        DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
                        ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
                        ZeZonesSourcePath = zeZonesPath,
                    });
                });
            });

        var client = factory.CreateClient();

        var metadata = await client.GetFromJsonAsync<MetadataResponse>("/api/metadata");
        Assert.NotNull(metadata);
        Assert.True(metadata.DataAvailable);
        Assert.Equal(20, metadata.StopCount);
        Assert.Contains("Test ZE-zone", metadata.ZeZones);

        var summary = await PostAsync<SummaryResponse>(client, "/api/summary", new AnalysisFilter { VervoerderTypes = ["eigen"] });
        Assert.Equal(12, summary.Stops);
        Assert.Equal(600, summary.TotalKm);

        var zeSummary = await PostAsync<SummaryResponse>(client, "/api/summary", new AnalysisFilter { ZeZoneMode = "in" });
        Assert.Equal(7, zeSummary.Stops);

        var stops = await PostAsync<StopMapResponse>(client, "/api/map/stops", new AnalysisFilter());
        Assert.NotEmpty(stops.HeatPoints);
        Assert.NotEmpty(stops.Markers);

        var roads = await PostAsync<RoadMapResponse>(client, "/api/map/roads", new AnalysisFilter { RoadThreshold = 1 });
        Assert.Equal("ok", roads.Status);
        Assert.NotEmpty(roads.Lines);

        var chargers = await PostAsync<ChargerMapResponse>(client, "/api/map/chargers", new ChargerFilter { MinPowerKw = 150 });
        Assert.Equal("ok", chargers.Status);
        Assert.NotEmpty(chargers.Markers);

        var overnight = await PostAsync<OvernightLocationsResponse>(client, "/api/overnight/locations", new OvernightLocationsRequest { MinVehicles = 1 });
        Assert.Equal("ok", overnight.Status);
        Assert.NotEmpty(overnight.Locations);

        var depotDetail = await PostAsync<SelectionDetailResponse>(client, "/api/overnight/location", new OvernightLocationDetailRequest
        {
            DepotId = overnight.Locations[0].DepotId
        });
        Assert.Equal("ok", depotDetail.Status);
        Assert.NotEmpty(depotDetail.Vehicles);

        var roadDetail = await PostAsync<SelectionDetailResponse>(client, "/api/roads/selection", new RoadSelectionRequest
        {
            Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100)
        });
        Assert.Equal("ok", roadDetail.Status);
        Assert.NotEmpty(roadDetail.Vehicles);
        Assert.Equal(1, roadDetail.DailyDistanceDistribution.Trips);
        Assert.Equal(15, roadDetail.DailyDistanceDistribution.Buckets.Length);

        var breakDemand = await PostAsync<RoadBreakDemandMapResponse>(client, "/api/roads/break-demand", new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            KwhPerKm = 1
        });
        Assert.Equal("ok", breakDemand.Status);
        Assert.NotEmpty(breakDemand.Lines);

        var breakDemandDetail = await PostAsync<RoadBreakDemandDetailResponse>(client, "/api/roads/break-demand/detail", new RoadBreakDemandDetailRequest
        {
            Road = new RoadSelection(52.0, 5.0, 52.02, 5.02, 3),
            KwhPerKm = 1
        });
        Assert.Equal("ok", breakDemandDetail.Status);
        Assert.NotEmpty(breakDemandDetail.VehiclesInWindow);

        var stopDetail = await PostAsync<SelectionDetailResponse>(client, "/api/stops/location", new StopLocationDetailRequest
        {
            Lat = 52.000,
            Lon = 5.000,
            RadiusKm = 0.5,
            Label = "Depot A"
        });
        Assert.Equal("ok", stopDetail.Status);
        Assert.Equal("stop", stopDetail.SelectionType);
        Assert.NotEmpty(stopDetail.HeatPoints);

        var power = await PostAsync<PowerProfileResponse>(client, "/api/power/profiles", new PowerProfileRequest { TopLocations = 5 });
        Assert.NotNull(power);
        Assert.NotEqual("missing", power.Status);

        var diagnostics = await PostAsync<PowerDiagnosticsResponse>(client, "/api/power/diagnostics", new AnalysisFilter());
        Assert.NotNull(diagnostics);
        Assert.NotEmpty(diagnostics.Assumptions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object payload)
    {
        var response = await client.PostAsJsonAsync(path, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
