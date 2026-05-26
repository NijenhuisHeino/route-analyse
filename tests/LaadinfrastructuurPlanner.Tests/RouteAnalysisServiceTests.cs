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
        var zeZonesPath = Path.Combine(cacheDir, "zez_pc6.csv");
        File.WriteAllText(
            zeZonesPath,
            "pc6,ze_zone,ze_startdatum\n1234AB,Test ZE-zone,2025-01-01\n");

        var options = new RouteAnalysisOptions
        {
            RepoRoot = _root,
            CacheDir = cacheDir,
            UploadedDatasetDir = Path.Combine(cacheDir, "uploaded-dataset", "active"),
            DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
            ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
            ZeZonesSourcePath = zeZonesPath,
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
        Assert.Equal(10, metadata.StopCount);
        Assert.Equal(new DateOnly(2026, 1, 1), metadata.MinDate);
        Assert.Equal(new DateOnly(2026, 1, 2), metadata.MaxDate);
        Assert.Contains("eigen", metadata.VervoerderTypes);
        Assert.Contains("charter", metadata.VervoerderTypes);
        Assert.Contains("Test ZE-zone", metadata.ZeZones);

        var eigen = await _service.GetSummaryAsync(new AnalysisFilter { VervoerderTypes = ["eigen"] });
        Assert.Equal(6, eigen.Stops);
        Assert.Equal(2, eigen.Trips);
        Assert.Equal(300, eigen.TotalKm);

        var dateFiltered = await _service.GetSummaryAsync(new AnalysisFilter { DateFrom = new DateOnly(2026, 1, 2) });
        Assert.Equal(7, dateFiltered.Stops);
        Assert.Equal(3, dateFiltered.Trips);
        Assert.Equal(460, dateFiltered.TotalKm);
    }

    [Fact]
    public async Task ZeroEmissionZoneFilterUsesPc6Lookup()
    {
        var inZone = await _service.GetSummaryAsync(new AnalysisFilter { ZeZoneMode = "in" });

        Assert.Equal(4, inZone.Stops);
        Assert.Equal(2, inZone.Trips);
        Assert.Equal(1, inZone.Wagens);

        var outsideZone = await _service.GetSummaryAsync(new AnalysisFilter { ZeZoneMode = "out" });

        Assert.Equal(6, outsideZone.Stops);
        Assert.Equal(4, outsideZone.Trips);

        var dashboard = await _service.GetDashboardAsync(new AnalysisFilter { ZeZoneMode = "in" });
        var zone = Assert.Single(dashboard.ZeZones);
        Assert.Equal("Test ZE-zone", zone.Zone);
        Assert.Equal("2025-01-01", zone.StartDate);
        Assert.Equal(4, zone.Stops);
    }

    [Fact]
    public async Task RoadMapUsesPrecomputedFullVariant()
    {
        var roads = await _service.GetRoadMapAsync(new AnalysisFilter { RoadThreshold = 1 });

        Assert.Equal("ok", roads.Status);
        Assert.Equal("full", roads.Variant);
        Assert.NotEmpty(roads.Lines);
        Assert.NotEmpty(roads.HeatPoints);
        var line = Assert.Single(roads.Lines);
        Assert.Equal(2, line.RawSegments);
        Assert.Contains("richting", line.Direction, StringComparison.OrdinalIgnoreCase);
        Assert.True(line.LengthKm > 0);
        Assert.NotNull(line.Coordinates);
        Assert.True(line.Coordinates!.Length >= 3);
    }

    [Fact]
    public async Task RoadMapFiltersLinesAndHeatByPassages()
    {
        var roads = await _service.GetRoadMapAsync(new AnalysisFilter { RoadThreshold = 6 });

        Assert.Equal("ok", roads.Status);
        var line = Assert.Single(roads.Lines);
        Assert.Equal(7, line.Passes);
        Assert.Equal(1, line.RawSegments);
        Assert.Empty(roads.HeatPoints);

        var hidden = await _service.GetRoadMapAsync(new AnalysisFilter { RoadThreshold = 8 });

        Assert.Equal("ok", hidden.Status);
        Assert.Empty(hidden.Lines);
        Assert.Empty(hidden.HeatPoints);
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
        Assert.Equal(depot.TotalMwh, depot.ShortageMwh);

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
        Assert.Equal(detail.Charging.TotalMwh, detail.Charging.ShortageMwh);
        Assert.All(detail.Charging.BusyWindows, window => Assert.Equal(window.DemandKwh, window.ShortageKwh));
        Assert.Contains("Publieke laadvraag", detail.Charging.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(24, detail.Charging.HourlyProfile.Length);
        Assert.Equal(168, detail.Charging.WeeklyProfile.Length);
        Assert.Contains(detail.Charging.HourlyProfile, hour => hour.Hour == 18 && hour.Vehicles == 1 && hour.RequiredKw == 17);
        Assert.Contains(detail.Charging.WeeklyProfile, cell => cell.DayLabel == "Donderdag" && cell.Hour == 18 && cell.Vehicles == 1 && cell.RequiredKw == 17 && cell.Wagencodes.Contains("W1"));
        var thursday18 = Assert.Single(detail.Charging.WeeklyProfile, cell => cell.DayLabel == "Donderdag" && cell.Hour == 18);
        var vehicleDemand = Assert.Single(thursday18.VehicleDemands);
        Assert.Equal("W1", vehicleDemand.Wagencode);
        Assert.Equal("-", vehicleDemand.Kenteken);
        Assert.Equal(17, vehicleDemand.RequiredKw);
        Assert.Equal(200, vehicleDemand.DemandKwh);
        Assert.Equal(12, vehicleDemand.StandingHours);
        Assert.Contains("18:00-06:00", vehicleDemand.Window);
        Assert.Contains(detail.Charging.WeeklyProfile, cell => cell.Vehicles == 0 && cell.RequiredKw == 0);
        var vehicle = Assert.Single(detail.Vehicles);
        Assert.Equal(1, vehicle.Days);
        Assert.Equal(180, vehicle.AvgDayKm);
        Assert.Equal(180, vehicle.AvgKwhPerDay);
        Assert.Equal(12, vehicle.AvgStandingHours);
        Assert.Equal(17, vehicle.RequiredKw);
    }

    [Fact]
    public async Task StopLocationDetailUsesDepartureDaysAfterFixedStandingPeriod()
    {
        var detail = await _service.GetStopLocationDetailAsync(new StopLocationDetailRequest
        {
            Lat = 52.000,
            Lon = 5.000,
            RadiusKm = 0.5,
            Label = "Depot A",
            Scenario = new ChargingScenario { KwhPerKm = 1.0, CapacityKwh = 200, TargetSocPct = 80, MinSocPct = 15 }
        });

        Assert.Equal("ok", detail.Status);
        Assert.Equal("stop", detail.SelectionType);
        Assert.StartsWith("Vertrekritten vanaf Depot A", detail.Title, StringComparison.Ordinal);
        Assert.Equal(1, detail.Summary.Trips);
        Assert.Equal(180, detail.Distribution.P95Km);
        Assert.NotEmpty(detail.HeatPoints);
        Assert.Contains(detail.HeatPoints, point => point.Lat == 52.2 && point.Lon == 5.2);
        Assert.Contains(detail.Charging.HourlyProfile, hour => hour.Hour == 18 && hour.Vehicles == 1 && hour.RequiredKw == 17);
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
        Assert.Equal(2, selection.Summary.Trips);
        Assert.True(selection.Distribution.P95Km >= 120);
        Assert.Equal(1, selection.DailyDistanceDistribution.Trips);
        Assert.Equal(280, selection.DailyDistanceDistribution.P95Km);
        Assert.Equal(15, selection.DailyDistanceDistribution.Buckets.Length);
        Assert.Equal(0, selection.DailyDistanceDistribution.Buckets[0].FromKm);
        Assert.Equal(50, selection.DailyDistanceDistribution.Buckets[0].ToKm);
        Assert.Equal(700, selection.DailyDistanceDistribution.Buckets[^1].FromKm);
        Assert.Equal(750, selection.DailyDistanceDistribution.Buckets[^1].ToKm);
        Assert.Single(selection.DailyDistanceDistribution.Buckets, x => x.Trips > 0);
        Assert.Contains(selection.DailyDistanceDistribution.Buckets, x => x.FromKm == 250 && x.ToKm == 300 && x.Trips == 1);
        var vehicle = Assert.Single(selection.Vehicles);
        Assert.Equal("W2", vehicle.Wagencode);
        Assert.Equal(2, vehicle.Passages);
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
