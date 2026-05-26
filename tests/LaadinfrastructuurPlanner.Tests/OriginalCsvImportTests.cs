using DuckDB.NET.Data;
using Microsoft.Extensions.Caching.Memory;
using LaadinfrastructuurPlanner.Models;
using LaadinfrastructuurPlanner.Services;

namespace LaadinfrastructuurPlanner.Tests;

public sealed class OriginalCsvImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "route-analysis-csv-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OriginalMonthlyCsvsAreMaterializedIntoPlannerCache()
    {
        var cacheDir = Path.Combine(_root, ".cache");
        var csvDir = Path.Combine(_root, "original-csv");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(csvDir);
        WriteOriginalCsv(Path.Combine(csvDir, "Rittendata per wagen_detail_jan.csv"));
        WriteGeocodeParquet(Path.Combine(cacheDir, "geocode_addresses.parquet"));

        var options = new RouteAnalysisOptions
        {
            RepoRoot = _root,
            CacheDir = cacheDir,
            UploadedDatasetDir = Path.Combine(cacheDir, "uploaded-dataset", "active"),
            DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
            ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
            OriginalCsvDir = csvDir,
        };
        var service = new RouteAnalysisService(new DuckDbRouteStore(options), new MemoryCache(new MemoryCacheOptions()));

        var metadata = await service.GetMetadataAsync();
        Assert.True(metadata.DataAvailable);
        Assert.Equal("Ritdata (1 bestand)", metadata.DataSource);
        Assert.Equal(5, metadata.StopCount);
        Assert.Equal(new DateOnly(2026, 1, 1), metadata.MinDate);
        Assert.Equal(new DateOnly(2026, 1, 2), metadata.MaxDate);

        var summary = await service.GetSummaryAsync(new AnalysisFilter { VervoerderTypes = ["eigen"] });
        Assert.Equal(5, summary.Stops);
        Assert.Equal(2, summary.Trips);
        Assert.Equal(300.5, summary.TotalKm);

        var locations = await service.GetOvernightLocationsAsync(new OvernightLocationsRequest
        {
            MinVehicles = 1,
            Scenario = new ChargingScenario { KwhPerKm = 1.0, CapacityKwh = 200, TargetSocPct = 80, MinSocPct = 15 }
        });
        var depot = Assert.Single(locations.Locations);
        Assert.Equal("auto:52.0000:5.0000", depot.DepotId);
        Assert.Equal(180, depot.P95DayKm);

        var detail = await service.GetOvernightLocationDetailAsync(new OvernightLocationDetailRequest { DepotId = depot.DepotId });
        Assert.Equal("11-BZX-8", Assert.Single(detail.Vehicles).Kentekens);

        var plateSummary = await service.GetSummaryAsync(new AnalysisFilter { Wagencodes = ["11BZX8"] });
        Assert.Equal(5, plateSummary.Stops);
        Assert.Equal(300.5, plateSummary.TotalKm);
    }

    [Fact]
    public async Task GenericUploadedCsvWithCoordinatesCanBeMaterialized()
    {
        var cacheDir = Path.Combine(_root, ".cache-generic");
        var uploadDir = Path.Combine(cacheDir, "uploaded-dataset", "active");
        Directory.CreateDirectory(uploadDir);
        File.WriteAllLines(Path.Combine(uploadDir, "routes.csv"),
        [
            "vehicle_id,trip_id,trip_date,planned_start,planned_end,lat,lon,address,distance_km,carrier_type,license_plate",
            "TRUCK-1,RIT-1,2026-02-01,2026-02-01 08:00:00,2026-02-01 08:15:00,52.10,5.10,Depot,42,eigen,AB-12-CD",
            "TRUCK-1,RIT-1,2026-02-01,2026-02-01 09:00:00,2026-02-01 09:10:00,52.20,5.20,Klant,42,eigen,AB-12-CD"
        ]);

        var options = new RouteAnalysisOptions
        {
            RepoRoot = _root,
            CacheDir = cacheDir,
            UploadedDatasetDir = uploadDir,
            DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
            ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
        };
        var service = new RouteAnalysisService(new DuckDbRouteStore(options), new MemoryCache(new MemoryCacheOptions()));

        var metadata = await service.GetMetadataAsync();
        Assert.True(metadata.DataAvailable);
        Assert.Equal("Eigen dataset", metadata.DataSource);
        Assert.Equal(2, metadata.StopCount);
        Assert.Equal(new DateOnly(2026, 2, 1), metadata.MinDate);

        var summary = await service.GetSummaryAsync(new AnalysisFilter());
        Assert.Equal(1, summary.Trips);
        Assert.Equal(42, summary.TotalKm);
    }

    [Fact]
    public async Task OriginalCsvWaitAndPauseActionsProduceHourlyPowerProfiles()
    {
        var cacheDir = Path.Combine(_root, ".cache-power");
        var csvDir = Path.Combine(_root, "original-csv-power");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(csvDir);
        WritePowerProfileCsv(Path.Combine(csvDir, "Rittendata per wagen_detail_jan.csv"));
        WritePowerGeocodeParquet(Path.Combine(cacheDir, "geocode_addresses.parquet"));

        var options = new RouteAnalysisOptions
        {
            RepoRoot = _root,
            CacheDir = cacheDir,
            UploadedDatasetDir = Path.Combine(cacheDir, "uploaded-dataset", "active"),
            DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
            ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
            OriginalCsvDir = csvDir,
        };
        var service = new RouteAnalysisService(new DuckDbRouteStore(options), new MemoryCache(new MemoryCacheOptions()));

        var profiles = await service.GetPowerProfilesAsync(new PowerProfileRequest
        {
            DateFrom = new DateOnly(2026, 1, 1),
            DateTo = new DateOnly(2026, 1, 2),
            VehicleClasses = ["own"],
            TopLocations = 5,
            CapacityKwh = 590,
        });

        Assert.Equal("ok", profiles.Status);
        var nieuwegein = Assert.Single(profiles.Locations);
        Assert.Contains("Groteweerd", nieuwegein.Address, StringComparison.OrdinalIgnoreCase);
        // Smart/spreidende laadcurve: demand = volledige capaciteit (590 kWh) per voertuig.
        // Window 22:15 → 01:30 = 3.25h. avgPower = min(400, 590/3.25) = 181.5 kW gespreid.
        // Hour 22 (overlap 0.75h) = 136 kW. Hour 23, 0 (overlap 1h) = 182 kW. Hour 1 (overlap 0.5h) = 91 kW.
        Assert.Equal(182, nieuwegein.PeakKw);
        Assert.Equal(1, nieuwegein.UniqueOwnVehicles);
        Assert.Equal(0, nieuwegein.UniqueCharterVehicles);
        Assert.Equal(24, nieuwegein.HourlyProfile.Length);
        Assert.Contains(nieuwegein.HourlyProfile, h => h.Hour == 22 && h.RequiredKw == 136 && h.Vehicles == 1);
        Assert.Contains(nieuwegein.HourlyProfile, h => h.Hour == 23 && h.RequiredKw == 182);
        Assert.Contains(nieuwegein.HourlyProfile, h => h.Hour == 0 && h.RequiredKw == 182);
        Assert.Contains(nieuwegein.HourlyProfile, h => h.Hour == 1 && h.RequiredKw == 91);
        Assert.Contains(profiles.Heatmap, h => h.LocationId == nieuwegein.LocationId && h.Hour == 23 && h.RequiredKw == 182);

        var detail = await service.GetPowerLocationProfileAsync(new PowerLocationProfileRequest
        {
            LocationId = nieuwegein.LocationId,
            VehicleClasses = ["own"],
            ScenarioYears = [2027, 2030],
            ScenarioMode = "linear",
            CapacityKwh = 590,
        });

        Assert.Equal("ok", detail.Status);
        Assert.Equal(nieuwegein.LocationId, detail.Profile?.LocationId);
        var hour23 = Assert.Single(detail.Profile!.HourlyProfile, h => h.Hour == 23);
        var powerVehicle = Assert.Single(hour23.VehicleDemands);
        Assert.Equal("001", powerVehicle.Wagencode);
        Assert.Equal("11-BZX-8", powerVehicle.Kenteken);
        Assert.Equal("own", powerVehicle.VehicleClass);
        // Hour 23 krijgt avgPower (181.5 kW) × 1h = 181.5 kWh aan demand.
        Assert.InRange(powerVehicle.DemandKwh, 180, 185);
        Assert.InRange(powerVehicle.RequiredKw, 180, 185);
        Assert.Equal(new DateOnly(2026, 1, 1), hour23.Date);
        Assert.NotEmpty(detail.DailyMetrics);
        Assert.Contains(detail.Scenarios, s => s.Year == 2027 && s.HourlyProfile.Length == 24);
        Assert.Contains(detail.Scenarios, s => s.Year == 2030 && s.Mode == "linear");

        var cappedCharter = await service.GetPowerProfilesAsync(new PowerProfileRequest
        {
            DateFrom = new DateOnly(2026, 1, 1),
            DateTo = new DateOnly(2026, 1, 2),
            VehicleClasses = ["charter"],
            TopLocations = 5,
            CapacityKwh = 590,
            MaxVehicleKw = 400,
        });
        var cappedDepot = Assert.Single(cappedCharter.Locations);
        Assert.Equal("Depot B", cappedDepot.Address);
        // Charter pause 12:00-12:45 (45 min). Demand = capacity = 590 kWh.
        // Time-to-full @ 400 kW = 1.475 h, gelimiteerd door 0.75h presence.
        // Energy hour 12 = 400 × 0.75 = 300 kWh → 300 kW avg.
        Assert.InRange(cappedDepot.PeakKw, 295, 305);

        var mcsCharter = await service.GetPowerProfilesAsync(new PowerProfileRequest
        {
            DateFrom = new DateOnly(2026, 1, 1),
            DateTo = new DateOnly(2026, 1, 2),
            VehicleClasses = ["charter"],
            TopLocations = 5,
            CapacityKwh = 590,
            MaxVehicleKw = 1500,
        });
        var mcsDepot = Assert.Single(mcsCharter.Locations);
        // MCS 1500 kW: time-to-full = 590/1500 = 0.393 h. Volle 590 kWh in 1 hour-slot → 590 kW avg.
        Assert.InRange(mcsDepot.PeakKw, 585, 595);

        var diagnostics = await service.GetPowerDiagnosticsAsync(new AnalysisFilter());
        Assert.True(diagnostics.RoutesWithoutWaitWindow > 0);
        Assert.Contains(diagnostics.VehicleClassCounts, x => x.VehicleClass == "own");
        Assert.Contains(diagnostics.Assumptions, text => text.Contains("590", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteOriginalCsv(string path)
    {
        var header = Csv(
            "Voertuig Type Eigenaar",
            "Wagen Code",
            "Wagentype Omschrijving",
            "Tripnummer",
            "Gerealizeerd Kenteken",
            "Starttijd Trip",
            "Eindtijd Trip",
            "Totale duur",
            "Totale rijtijd",
            "Totale Afstand (KM)",
            "Actie soort",
            "Gepland vanaf (Trip actie)",
            "Gepland tot (Trip actie)",
            "Adres",
            "Dagorder Nummer",
            "Gewicht Dagorder (KG)",
            "RunningSum Gewicht (KG)");

        File.WriteAllLines(path,
        [
            header,
            Row("001", "T1", "11-BZX-8", "01-1-2026 08:00:00", "01-1-2026 18:00:00", "120,5", "01-1-2026 08:00:00", "01-1-2026 08:10:00", "Depot A"),
            Row("001", "T1", "11-BZX-8", "01-1-2026 08:00:00", "01-1-2026 18:00:00", "120,5", "01-1-2026 12:00:00", "01-1-2026 12:20:00", "Hub B"),
            Row("001", "T1", "11-BZX-8", "01-1-2026 08:00:00", "01-1-2026 18:00:00", "120,5", "01-1-2026 18:00:00", "01-1-2026 18:00:00", "Depot A"),
            Row("001", "T2", "11-BZX-8", "02-1-2026 06:00:00", "02-1-2026 15:00:00", "180", "02-1-2026 06:00:00", "02-1-2026 06:00:00", "Depot A"),
            Row("001", "T2", "11-BZX-8", "02-1-2026 06:00:00", "02-1-2026 15:00:00", "180", "02-1-2026 15:00:00", "02-1-2026 15:00:00", "Depot A")
        ]);
    }

    private static void WritePowerProfileCsv(string path)
    {
        var header = Csv(
            "Voertuig Type Eigenaar",
            "Wagen Code",
            "Wagentype Omschrijving",
            "Tripnummer",
            "Gerealizeerd Kenteken",
            "Starttijd Trip",
            "Eindtijd Trip",
            "Totale duur",
            "Totale rijtijd",
            "Totale Afstand (KM)",
            "Actie soort",
            "Gepland vanaf (Trip actie)",
            "Gepland tot (Trip actie)",
            "Adres",
            "Dagorder Nummer",
            "Gewicht Dagorder (KG)",
            "RunningSum Gewicht (KG)");

        File.WriteAllLines(path,
        [
            header,
            ActionRow("Eigen Vervoer", "001", "Trekker met oplegger", "T1", "11-BZX-8", "travel", "01-1-2026 22:00:00", "01-1-2026 22:15:00", "Groteweerd 80, Nieuwegein"),
            ActionRow("Eigen Vervoer", "001", "Trekker met oplegger", "T1", "11-BZX-8", "wait_task_available", "01-1-2026 23:30:00", "02-1-2026 01:30:00", "Groteweerd 80, Nieuwegein"),
            ActionRow("Eigen Vervoer", "001", "Trekker met oplegger", "T9", "11-BZX-8", "wait_after", "01-1-2026 23:45:00", "02-1-2026 00:15:00", "Groteweerd 80, Nieuwegein"),
            ActionRow("Uitbesteed Vervoer", "C01", "Bakwagen 9 ton - hoog", "T2", "22-BBB-2", "pause", "01-1-2026 12:00:00", "01-1-2026 12:45:00", "Depot B"),
            ActionRow("Eigen Vervoer", "002", "Bakwagen 9 ton - hoog", "T3", "33-CCC-3", "travel", "01-1-2026 08:00:00", "01-1-2026 08:15:00", "Depot C")
        ]);
    }

    private static string ActionRow(
        string carrier,
        string wagencode,
        string vehicleType,
        string tripId,
        string kenteken,
        string action,
        string plannedStart,
        string plannedEnd,
        string address)
    {
        return Csv(
            carrier,
            wagencode,
            vehicleType,
            tripId,
            kenteken,
            plannedStart,
            plannedEnd,
            "02:00:00",
            "00:15:00",
            "12",
            action,
            plannedStart,
            plannedEnd,
            address,
            "",
            "",
            "0");
    }

    private static string Row(
        string wagencode,
        string tripId,
        string kenteken,
        string tripStart,
        string tripEnd,
        string tripKm,
        string plannedStart,
        string plannedEnd,
        string address)
    {
        return Csv(
            "Eigen Vervoer",
            wagencode,
            "Trekker met oplegger",
            tripId,
            kenteken,
            tripStart,
            tripEnd,
            "09:00:00",
            "00:00:00",
            tripKm,
            "travel",
            plannedStart,
            plannedEnd,
            address,
            "",
            "",
            "0");
    }

    private static string Csv(params string[] values)
    {
        return string.Join(",", values.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    }

    private static void WriteGeocodeParquet(string path)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection,
            """
            CREATE TABLE geocode AS
            SELECT * FROM (
                VALUES
                ('Depot A', 52.000, 5.000),
                ('Hub B', 52.500, 5.500)
            ) AS t(query, lat, lon);
            """);
        Execute(connection, $"COPY geocode TO '{SqlPath(path)}' (FORMAT PARQUET);");
    }

    private static void WritePowerGeocodeParquet(string path)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection,
            """
            CREATE TABLE geocode AS
            SELECT * FROM (
                VALUES
                ('Groteweerd 80, Nieuwegein', 52.030, 5.080),
                ('Depot B', 52.100, 5.100),
                ('Depot C', 52.200, 5.200)
            ) AS t(query, lat, lon);
            """);
        Execute(connection, $"COPY geocode TO '{SqlPath(path)}' (FORMAT PARQUET);");
    }

    private static string SqlPath(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
