using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using LaadinfrastructuurPlanner.Models;
using LaadinfrastructuurPlanner.Services;

namespace LaadinfrastructuurPlanner.Tests;

public sealed class RouteAnalysisServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "route-analysis-tests", Guid.NewGuid().ToString("N"));
    private readonly RouteAnalysisService _service;

    public RouteAnalysisServiceTests()
    {
        var cacheDir = Path.Combine(_root, ".cache");
        TestParquetData.WriteAll(cacheDir);

        var options = new RouteAnalysisOptions
        {
            RepoRoot = _root,
            CacheDir = cacheDir,
            UploadedDatasetDir = Path.Combine(cacheDir, "uploaded-dataset", "active"),
            DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
            ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
        };
        var store = new DuckDbRouteStore(options);
        _service = new RouteAnalysisService(store, new MemoryCache(new MemoryCacheOptions()));

        var warmup = new PlannerWarmupService(store, NullLogger<PlannerWarmupService>.Instance);
        warmup.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task MetadataAndSummaryHonorFilters()
    {
        var metadata = await _service.GetMetadataAsync();

        Assert.True(metadata.DataAvailable);
        Assert.Equal(8, metadata.StopCount);
        Assert.Equal(new DateOnly(2026, 1, 1), metadata.MinDate);
        Assert.Equal(new DateOnly(2026, 1, 2), metadata.MaxDate);
        Assert.Contains("eigen", metadata.VervoerderTypes);
        Assert.Contains("charter", metadata.VervoerderTypes);

        var eigen = await _service.GetSummaryAsync(new AnalysisFilter { VervoerderTypes = ["eigen"] });
        Assert.Equal(6, eigen.Stops);
        Assert.Equal(2, eigen.Trips);
        Assert.Equal(300, eigen.TotalKm);

        var dateFiltered = await _service.GetSummaryAsync(new AnalysisFilter { DateFrom = new DateOnly(2026, 1, 2) });
        Assert.Equal(5, dateFiltered.Stops);
        Assert.Equal(2, dateFiltered.Trips);
        Assert.Equal(380, dateFiltered.TotalKm);
    }

    [Fact]
    public async Task RoadMapUsesPrecomputedFullVariant()
    {
        var roads = await _service.GetRoadMapAsync(new AnalysisFilter { RoadThreshold = 1 });

        Assert.Equal("ok", roads.Status);
        Assert.Equal("full", roads.Variant);
        Assert.NotEmpty(roads.Lines);
        Assert.NotEmpty(roads.HeatPoints);
    }

    [Fact]
    public async Task RoadMapRejectsCustomFiltersWithoutPrecompute()
    {
        var roads = await _service.GetRoadMapAsync(new AnalysisFilter { Wagencodes = ["W1"] });

        Assert.Equal("cache_missing", roads.Status);
        Assert.Empty(roads.Lines);
    }

    [Fact]
    public async Task SimulationProducesChargeEventsAndHotspots()
    {
        var simulation = await _service.GetSimulationAsync(new SimulationRequest
        {
            KwhPerKm = 3.0,
            CapacityKwh = 100,
            StartSocPct = 100,
            ThresholdPct = 95,
            MaxChargeKw = 100
        });

        Assert.True(simulation.ChargeEvents > 0);
        Assert.NotEmpty(simulation.Hotspots);
        Assert.NotEmpty(simulation.TripsTop);
    }

    [Fact]
    public async Task ChargersCanBeFilteredByPowerAccessAndDedicated()
    {
        var publicChargers = await _service.GetChargersAsync(new ChargerFilter
        {
            MinPowerKw = 150,
            Access = ["Publiek"],
        });

        Assert.Equal("ok", publicChargers.Status);
        Assert.Single(publicChargers.Markers);
        Assert.Equal("Publieke HPC", publicChargers.Markers[0].Name);

        var dedicated = await _service.GetChargersAsync(new ChargerFilter
        {
            MinPowerKw = 400,
            OnlyDedicated = true,
            Access = ["Privaat"],
        });

        Assert.Single(dedicated.Markers);
        Assert.Equal("Dedicated Depot", dedicated.Markers[0].Name);
    }

    [Fact]
    public async Task OvernightLocationsUseGapNearnessAndTripDistance()
    {
        var locations = await _service.GetOvernightLocationsAsync(new OvernightLocationsRequest
        {
            MinVehicles = 1,
            Scenario = new ChargingScenario { KwhPerKm = 1.0, CapacityKwh = 200, TargetSocPct = 80, MinSocPct = 15 }
        });

        Assert.Equal("ok", locations.Status);
        var depot = Assert.Single(locations.Locations);
        Assert.Equal("auto:52.000:5.000", depot.DepotId);
        Assert.Equal(1, depot.UniqueVehicles);
        Assert.Equal(180, depot.P95DayKm);
        Assert.True(depot.TotalMwh > 0);

        var detail = await _service.GetOvernightLocationDetailAsync(new OvernightLocationDetailRequest
        {
            DepotId = depot.DepotId,
            Scenario = new ChargingScenario { KwhPerKm = 1.0, CapacityKwh = 200, TargetSocPct = 80, MinSocPct = 15 }
        });

        Assert.Equal("ok", detail.Status);
        Assert.Equal(1, detail.Summary.Trips);
        Assert.Equal(180, detail.Distribution.P95Km);
        Assert.NotEmpty(detail.HeatPoints);
        Assert.NotEmpty(detail.Charging.BusyWindows);
        Assert.Equal(24, detail.Charging.HourlyProfile.Length);
        Assert.Contains(detail.Charging.HourlyProfile, hour => hour.Hour == 18 && hour.Vehicles == 1 && hour.RequiredKw == 15);
        var vehicle = Assert.Single(detail.Vehicles);
        Assert.Equal(1, vehicle.Days);
        Assert.Equal(180, vehicle.AvgDayKm);
        Assert.Equal(180, vehicle.AvgKwhPerDay);
        Assert.Equal(12, vehicle.AvgStandingHours);
        Assert.Equal(15, vehicle.RequiredKw);
    }

    [Fact]
    public async Task RoadSelectionReturnsTripDistribution()
    {
        var selection = await _service.GetRoadSelectionAsync(new RoadSelectionRequest
        {
            Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100),
            Scenario = new ChargingScenario { KwhPerKm = 1.0 }
        });

        Assert.Equal("ok", selection.Status);
        Assert.True(selection.Summary.Trips >= 1);
        Assert.True(selection.Distribution.P95Km >= 120);
        Assert.NotEmpty(selection.HeatPoints);
        Assert.Contains("corridor", selection.Charging.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WarmApiCallsStayUnderTwoSecondsOnSyntheticCache()
    {
        var filter = new AnalysisFilter { RoadThreshold = 1 };

        await _service.GetMetadataAsync();
        await _service.GetSummaryAsync(filter);
        await _service.GetStopMapAsync(filter);
        await _service.GetRoadMapAsync(filter);
        await _service.GetChargersAsync(new ChargerFilter());
        await _service.GetOvernightLocationsAsync(new OvernightLocationsRequest { MinVehicles = 1 });
        await _service.GetRoadSelectionAsync(new RoadSelectionRequest { Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100) });

        var sw = Stopwatch.StartNew();
        await _service.GetSummaryAsync(filter);
        await _service.GetStopMapAsync(filter);
        await _service.GetRoadMapAsync(filter);
        await _service.GetChargersAsync(new ChargerFilter());
        await _service.GetOvernightLocationsAsync(new OvernightLocationsRequest { MinVehicles = 1 });
        await _service.GetRoadSelectionAsync(new RoadSelectionRequest { Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100) });
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Warm calls took {sw.Elapsed}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

}
