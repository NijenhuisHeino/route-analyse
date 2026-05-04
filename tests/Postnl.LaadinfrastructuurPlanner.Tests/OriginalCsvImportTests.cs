using DuckDB.NET.Data;
using Microsoft.Extensions.Caching.Memory;
using Postnl.LaadinfrastructuurPlanner.Models;
using Postnl.LaadinfrastructuurPlanner.Services;

namespace Postnl.LaadinfrastructuurPlanner.Tests;

public sealed class OriginalCsvImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "postnl-planner-csv-tests", Guid.NewGuid().ToString("N"));

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
            DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
            ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
            OriginalCsvDir = csvDir,
        };
        var service = new RouteAnalysisService(new DuckDbRouteStore(options), new MemoryCache(new MemoryCacheOptions()));

        var metadata = await service.GetMetadataAsync();
        Assert.True(metadata.DataAvailable);
        Assert.Equal("Ritdata 2025 (1 maand)", metadata.DataSource);
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
        Assert.Equal("auto:52.000:5.000", depot.DepotId);
        Assert.Equal(180, depot.P95DayKm);

        var detail = await service.GetOvernightLocationDetailAsync(new OvernightLocationDetailRequest { DepotId = depot.DepotId });
        Assert.Equal("11-BZX-8", Assert.Single(detail.Vehicles).Kentekens);

        var plateSummary = await service.GetSummaryAsync(new AnalysisFilter { Wagencodes = ["11BZX8"] });
        Assert.Equal(5, plateSummary.Stops);
        Assert.Equal(300.5, plateSummary.TotalKm);
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

    private static string SqlPath(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
