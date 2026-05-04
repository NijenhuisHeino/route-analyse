using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Postnl.RouteAnalyse.Fast.Models;
using Postnl.RouteAnalyse.Fast.Services;

namespace Postnl.RouteAnalyse.Fast.Tests;

public sealed class FastApiEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "postnl-fast-api-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FastApiEndpointsReturnExpectedJson()
    {
        var cacheDir = Path.Combine(_root, ".cache");
        TestParquetData.WriteAll(cacheDir);

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
                        DuckDbPath = Path.Combine(cacheDir, "fast", "route-analysis.duckdb"),
                        ManifestPath = Path.Combine(cacheDir, "fast", "manifest.json"),
                    });
                });
            });

        var client = factory.CreateClient();

        var metadata = await client.GetFromJsonAsync<MetadataResponse>("/fast/api/metadata");
        Assert.NotNull(metadata);
        Assert.True(metadata.DataAvailable);
        Assert.Equal(8, metadata.StopCount);

        var summary = await PostAsync<SummaryResponse>(client, "/fast/api/summary", new AnalysisFilter { VervoerderTypes = ["eigen"] });
        Assert.Equal(6, summary.Stops);
        Assert.Equal(300, summary.TotalKm);

        var stops = await PostAsync<StopMapResponse>(client, "/fast/api/map/stops", new AnalysisFilter());
        Assert.NotEmpty(stops.HeatPoints);
        Assert.NotEmpty(stops.Markers);

        var roads = await PostAsync<RoadMapResponse>(client, "/fast/api/map/roads", new AnalysisFilter { RoadThreshold = 1 });
        Assert.Equal("ok", roads.Status);
        Assert.NotEmpty(roads.Lines);

        var chargers = await PostAsync<ChargerMapResponse>(client, "/fast/api/map/chargers", new ChargerFilter { MinPowerKw = 150 });
        Assert.Equal("ok", chargers.Status);
        Assert.NotEmpty(chargers.Markers);

        var overnight = await PostAsync<OvernightLocationsResponse>(client, "/fast/api/overnight/locations", new OvernightLocationsRequest { MinVehicles = 1 });
        Assert.Equal("ok", overnight.Status);
        Assert.NotEmpty(overnight.Locations);

        var depotDetail = await PostAsync<SelectionDetailResponse>(client, "/fast/api/overnight/location", new OvernightLocationDetailRequest
        {
            DepotId = overnight.Locations[0].DepotId
        });
        Assert.Equal("ok", depotDetail.Status);
        Assert.NotEmpty(depotDetail.Vehicles);

        var roadDetail = await PostAsync<SelectionDetailResponse>(client, "/fast/api/roads/selection", new RoadSelectionRequest
        {
            Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100)
        });
        Assert.Equal("ok", roadDetail.Status);
        Assert.NotEmpty(roadDetail.Vehicles);
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
