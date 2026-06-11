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
        Assert.Equal(20, metadata.StopCount);
        Assert.Equal(new DateOnly(2026, 1, 1), metadata.MinDate);
        Assert.Equal(new DateOnly(2026, 1, 3), metadata.MaxDate);
        Assert.Contains("eigen", metadata.VervoerderTypes);
        Assert.Contains("charter", metadata.VervoerderTypes);
        Assert.Contains("Test ZE-zone", metadata.ZeZones);

        var eigen = await _service.GetSummaryAsync(new AnalysisFilter { VervoerderTypes = ["eigen"] });
        Assert.Equal(12, eigen.Stops);
        Assert.Equal(5, eigen.Trips);
        Assert.Equal(600, eigen.TotalKm);

        var dateFiltered = await _service.GetSummaryAsync(new AnalysisFilter { DateFrom = new DateOnly(2026, 1, 2) });
        Assert.Equal(17, dateFiltered.Stops);
        Assert.Equal(8, dateFiltered.Trips);
        Assert.Equal(1030, dateFiltered.TotalKm);
    }

    [Fact]
    public async Task ZeroEmissionZoneFilterUsesPc6Lookup()
    {
        var inZone = await _service.GetSummaryAsync(new AnalysisFilter { ZeZoneMode = "in" });

        Assert.Equal(7, inZone.Stops);
        Assert.Equal(5, inZone.Trips);
        Assert.Equal(2, inZone.Wagens);

        var outsideZone = await _service.GetSummaryAsync(new AnalysisFilter { ZeZoneMode = "out" });

        Assert.Equal(13, outsideZone.Stops);
        Assert.Equal(9, outsideZone.Trips);

        var dashboard = await _service.GetDashboardAsync(new AnalysisFilter { ZeZoneMode = "in" });
        var zone = Assert.Single(dashboard.ZeZones);
        Assert.Equal("Test ZE-zone", zone.Zone);
        Assert.Equal("2025-01-01", zone.StartDate);
        Assert.Equal(7, zone.Stops);
    }

    [Fact]
    public async Task RoadMapUsesPrecomputedFullVariant()
    {
        var roads = await _service.GetRoadMapAsync(new AnalysisFilter { RoadThreshold = 1 });

        Assert.Equal("ok", roads.Status);
        Assert.Equal("full", roads.Variant);
        Assert.NotEmpty(roads.Lines);
        Assert.NotEmpty(roads.HeatPoints);
        Assert.Contains(roads.Lines, candidate => candidate.RawSegments >= 2);
        var line = roads.Lines.First(candidate => candidate.RawSegments >= 2);
        Assert.Equal(2, line.RawSegments);
        Assert.Contains("richting", line.Direction, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(line.RoadName));
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

        var fourConnectors = await _service.GetChargersAsync(new ChargerFilter
        {
            MinPowerKw = 350,
            MinConnectors = 4,
            Access = ["Publiek", "Privaat"],
        });

        Assert.Equal(2, fourConnectors.Markers.Length);

        var eightConnectors = await _service.GetChargersAsync(new ChargerFilter
        {
            MinPowerKw = 350,
            MinConnectors = 8,
            Access = ["Publiek", "Privaat"],
        });

        var eightConnectorMarker = Assert.Single(eightConnectors.Markers);
        Assert.Equal("Publieke HPC", eightConnectorMarker.Name);

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
    public async Task RoadBreakDemandDefaultsExposeWindowAndDiagnostics()
    {
        var demand = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            KwhPerKm = 1.0
        });

        Assert.Equal("ok", demand.Status);
        Assert.Equal(3.5, demand.WindowStartHours);
        Assert.Equal(4.5, demand.WindowEndHours);
        Assert.Equal(0.75, demand.BreakDurationHours);
        Assert.True(demand.Diagnostics.TotalTrips >= 0);
        Assert.NotNull(demand.Lines);
    }

    [Fact]
    public async Task RoadBreakDemandCarriesDriveTimeAcrossTripsInSameShift()
    {
        var demand = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            KwhPerKm = 1.0,
            WindowStartHours = 3.5,
            WindowEndHours = 4.5,
            BreakDurationHours = 0.75,
            ShiftResetGapHours = 2.0
        });

        Assert.Contains(demand.Lines, line => line.Passages > 0 && line.TotalKwh > 0 && line.PeakMw > 0);
        Assert.Contains(demand.Diagnostics.ExclusionReasons, reason => reason.Contains("included", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RoadBreakDemandTopPercentRanksVisibleLinesByPausePassages()
    {
        var all = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            RoadTopPercent = 100,
            KwhPerKm = 1.0
        });
        var topOnePercent = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            RoadTopPercent = 1,
            KwhPerKm = 1.0
        });

        Assert.True(all.Lines.Length > topOnePercent.Lines.Length);
        Assert.NotEmpty(topOnePercent.Lines);
        Assert.All(topOnePercent.Lines, line => Assert.Equal(all.Lines.Max(x => x.Passages), line.Passages));
    }

    [Fact]
    public async Task RoadBreakDemandLegendReflectsVisibleLines()
    {
        var demand = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            RoadTopPercent = 100,
            KwhPerKm = 1.0
        });

        var legend = demand.Legend;
        Assert.NotNull(legend);
        Assert.NotEmpty(demand.Lines);
        Assert.Equal(demand.Lines.Min(x => x.Passages), legend.MinPassages);
        Assert.Equal(demand.Lines.Max(x => x.Passages), legend.MaxPassages);

        Assert.NotEmpty(legend.GradientStops);
        Assert.Equal(legend.MinPassages, legend.GradientStops[0].Passages);
        Assert.Equal(legend.MaxPassages, legend.GradientStops[^1].Passages);
        for (var i = 1; i < legend.GradientStops.Length; i++)
        {
            Assert.True(legend.GradientStops[i].Passages > legend.GradientStops[i - 1].Passages);
        }
        Assert.All(legend.GradientStops, stop => Assert.StartsWith("#", stop.Color));

        Assert.NotEmpty(legend.Bins);
        Assert.Equal(legend.MinPassages, legend.Bins[0].FromPassages);
        Assert.Equal(legend.MaxPassages, legend.Bins[^1].ToPassages);
        for (var i = 1; i < legend.Bins.Length; i++)
        {
            Assert.True(legend.Bins[i].FromPassages > legend.Bins[i - 1].ToPassages);
        }
        Assert.All(legend.Bins, bin => Assert.True(bin.FromPassages <= bin.ToPassages));
        Assert.Equal(demand.Lines.Length, legend.Bins.Sum(x => x.LineCount));
    }

    [Fact]
    public async Task RoadBreakDemandLegendCollapsesWhenAllPassagesEqual()
    {
        var demand = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            RoadTopPercent = 1,
            KwhPerKm = 1.0
        });

        var legend = demand.Legend;
        Assert.NotNull(legend);
        Assert.Equal(legend.MinPassages, legend.MaxPassages);
        var stop = Assert.Single(legend.GradientStops);
        Assert.Equal(legend.MaxPassages, stop.Passages);
        var bin = Assert.Single(legend.Bins);
        Assert.Equal("Alle wegvlakken", bin.Label);
        Assert.Equal(demand.Lines.Length, bin.LineCount);
    }

    [Fact]
    public async Task RoadBreakDemandLegendIsNullWithoutLines()
    {
        var demand = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
        {
            RoadThreshold = 1,
            RoadTopPercent = 0,
            KwhPerKm = 1.0
        });

        Assert.Empty(demand.Lines);
        Assert.Null(demand.Legend);
    }

    [Fact]
    public async Task RoadBreakDemandCapsVehicleEnergyAtTargetBatteryCapacity()
    {
        var detail = await _service.GetRoadBreakDemandDetailAsync(new RoadBreakDemandDetailRequest
        {
            Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100),
            KwhPerKm = 10.0,
            CapacityKwh = 200,
            TargetSocPct = 80,
            BreakDurationHours = 0.75
        });

        Assert.NotEmpty(detail.VehiclesInWindow);
        Assert.All(detail.VehiclesInWindow, row =>
        {
            Assert.True(row.DemandKwh <= 160, $"Demand for {row.Wagencode} was {row.DemandKwh} kWh");
            Assert.True(row.RequiredKw <= 213.4, $"Required power for {row.Wagencode} was {row.RequiredKw} kW");
        });
    }

    [Fact]
    public async Task RoadBreakDemandDetailReturnsTwentyFourHourHeatmapForSelectedRoad()
    {
        var detail = await _service.GetRoadBreakDemandDetailAsync(new RoadBreakDemandDetailRequest
        {
            Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100),
            KwhPerKm = 1.0,
            BreakDurationHours = 0.75
        });

        Assert.Equal("ok", detail.Status);
        Assert.Equal(24, detail.HourlyProfile.Length);
        Assert.Equal(Enumerable.Range(0, 24), detail.HourlyProfile.Select(x => x.Hour));
        Assert.Contains(detail.HourlyProfile, hour => hour.RequiredKw > 0 && hour.Vehicles > 0 && hour.DemandKwh > 0);
        Assert.Equal(detail.PeakMw, Math.Round(detail.HourlyProfile.Max(x => x.RequiredMw), 3));
    }

    [Fact]
    public async Task RoadBreakDemandDetailReturnsWeeklyPeakHeatmapForSelectedRoad()
    {
        var detail = await _service.GetRoadBreakDemandDetailAsync(new RoadBreakDemandDetailRequest
        {
            Road = new RoadSelection(53.0, 6.0, 51.9, 4.5, 100),
            KwhPerKm = 1.0,
            BreakDurationHours = 0.75
        });

        Assert.Equal("ok", detail.Status);
        Assert.Equal(7 * 24, detail.WeeklyProfile.Length);
        Assert.All(
            detail.WeeklyProfile.GroupBy(x => x.DayIndex),
            day => Assert.Equal(Enumerable.Range(0, 24), day.Select(x => x.Hour)));
        Assert.Contains(detail.WeeklyProfile, cell => cell.RequiredKw > 0 && cell.Vehicles > 0);

        var weeklyPeakKw = detail.WeeklyProfile.Max(x => x.RequiredKw);
        var quarterPeakKw = detail.QuarterProfile.Max(x => x.RequiredKw);
        Assert.True(
            Math.Abs(weeklyPeakKw - quarterPeakKw) <= 1,
            $"weekly peak {weeklyPeakKw} must match quarter peak {quarterPeakKw}");

        Assert.All(
            detail.WeeklyProfile.Where(x => x.RequiredKw > 0),
            cell => Assert.NotEmpty(cell.VehicleDemands));
    }

    [Fact]
    public async Task RoadBreakDemandResetsOnlyAfterLongGapAtResetLocation()
    {
        var detail = await _service.GetRoadBreakDemandDetailAsync(new RoadBreakDemandDetailRequest
        {
            Road = new RoadSelection(52.0, 5.0, 52.02, 5.02, 3),
            KwhPerKm = 1.0,
            ShiftResetGapHours = 2.0
        });

        Assert.Equal("ok", detail.Status);
        Assert.Contains(detail.VehiclesInWindow, row => row.Wagencode == "W3");
        Assert.Single(detail.VehiclesInWindow, row => row.Wagencode == "W3");
        Assert.All(detail.VehiclesInWindow, row => Assert.InRange(row.DriveHoursSinceShiftStart, 3.5, 4.5));

        var customerGapDetail = await _service.GetRoadBreakDemandDetailAsync(new RoadBreakDemandDetailRequest
        {
            Road = new RoadSelection(52.6, 5.6, 53.0, 6.0, 5),
            KwhPerKm = 1.0,
            ShiftResetGapHours = 2.0
        });

        Assert.Contains(
            customerGapDetail.VehiclesInWindow,
            row => row.Wagencode == "W4" && row.KmSinceShiftStart > 130 && row.KmSinceShiftStart <= 270);
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
