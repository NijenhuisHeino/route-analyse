using System.IO.Compression;
using System.Globalization;
using System.Net;
using DuckDB.NET.Data;
using LaadinfrastructuurPlanner.Models;
using LaadinfrastructuurPlanner.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LaadinfrastructuurPlanner.Tests;

public sealed class FleetDataServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-data-service-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetDepotsAsyncUsesTripEndpointAddressForFleetDepot()
    {
        var service = CreateService(
            fleetRows:
            [
                new("Depot A", "Pakketten", "1", "AA-11-AA")
            ],
            stopRows:
            [
                Stop("001", "T1", 0, "Adres A 1234 AB A", 52.0, 5.0),
                Stop("001", "T1", 1, "Hub B 5678 CD B", 52.5, 5.5),
                Stop("001", "T1", 2, "Adres A 1234 AB A", 52.0, 5.0),
                Stop("001", "T2", 0, "Adres A 1234 AB A", 52.0, 5.0),
                Stop("001", "T2", 1, "Hub C 5678 CD C", 52.2, 5.2),
                Stop("001", "T2", 2, "Adres A 1234 AB A", 52.0, 5.0),
            ],
            geocodeOverrides:
            [
                "Depot A,51.0,4.0,test"
            ]);

        var response = await service.GetDepotsAsync(CancellationToken.None);

        var depot = Assert.Single(response.Depots);
        Assert.Equal("Adres A 1234 AB A", depot.Address);
        Assert.Equal(52.0, depot.Lat);
        Assert.Equal(5.0, depot.Lon);
        Assert.Equal("exact", depot.MatchStatus);
        Assert.Equal(100, depot.MatchConfidencePct);
        Assert.Equal(4, depot.EvidenceEvents);
        Assert.Equal(1, depot.EvidenceVehicles);
        Assert.Empty(depot.AlternativeAddresses);
    }

    [Fact]
    public async Task GetDepotsAsyncPrefersAddressPlaceMatchOverHighestEndpointCount()
    {
        var service = CreateService(
            fleetRows:
            [
                new("Depot Den Bosch", "Pakketten", "2", "BB-22-BB")
            ],
            stopRows:
            [
                Stop("002", "T1", 0, "Hoekwei 6, 4004 LX Tiel", 51.9, 5.45),
                Stop("002", "T1", 1, "Klant 1, 5000 AA Tilburg", 51.6, 5.0),
                Stop("002", "T1", 2, "Hoekwei 6, 4004 LX Tiel", 51.9, 5.45),
                Stop("002", "T2", 0, "Ketelaarskampweg 4, 5222 AL 'S-Hertogenbosch", 51.70, 5.27),
                Stop("002", "T2", 1, "Klant 2, 5000 AA Tilburg", 51.6, 5.0),
                Stop("002", "T2", 2, "Hoekwei 6, 4004 LX Tiel", 51.9, 5.45),
                Stop("002", "T3", 0, "Ketelaarskampweg 4, 5222 AL 'S-Hertogenbosch", 51.70, 5.27),
                Stop("002", "T3", 1, "Klant 3, 5000 AA Tilburg", 51.6, 5.0),
                Stop("002", "T3", 2, "Hoekwei 6, 4004 LX Tiel", 51.9, 5.45),
            ],
            geocodeOverrides:
            [
                "Depot Den Bosch,51.0,4.0,test"
            ]);

        var response = await service.GetDepotsAsync(CancellationToken.None);

        var depot = Assert.Single(response.Depots);
        Assert.Equal("Ketelaarskampweg 4, 5222 AL 'S-Hertogenbosch", depot.Address);
        Assert.Equal("review", depot.MatchStatus);
        Assert.Equal(33.3, depot.MatchConfidencePct, precision: 1);
        Assert.Contains(depot.AlternativeAddresses, x => x.Address == "Hoekwei 6, 4004 LX Tiel");
    }

    [Fact]
    public async Task GetDepotsAsyncMarksAmbiguousDepotForReviewWithAlternatives()
    {
        var service = CreateService(
            fleetRows:
            [
                new("Depot Tilburg", "Pakketten", "3", "CC-33-CC")
            ],
            stopRows:
            [
                Stop("003", "T1", 0, "Doornepol 11, 5301 LV Zaltbommel", 51.80, 5.27),
                Stop("003", "T1", 1, "Klant 1, 5000 AA Tilburg", 51.6, 5.0),
                Stop("003", "T1", 2, "Doornepol 11, 5301 LV Zaltbommel", 51.80, 5.27),
                Stop("003", "T2", 0, "Ledeboerstraat 82, 5048 AD Tilburg", 51.58, 5.04),
                Stop("003", "T2", 1, "Klant 2, 5000 AA Tilburg", 51.6, 5.0),
                Stop("003", "T2", 2, "Doornepol 11, 5301 LV Zaltbommel", 51.80, 5.27),
            ],
            geocodeOverrides:
            [
                "Depot Tilburg,51.0,4.0,test"
            ]);

        var response = await service.GetDepotsAsync(CancellationToken.None);

        var depot = Assert.Single(response.Depots);
        Assert.Equal("review", depot.MatchStatus);
        Assert.Equal("Ledeboerstraat 82, 5048 AD Tilburg", depot.Address);
        Assert.Equal(25, depot.MatchConfidencePct);
        Assert.Contains(depot.AlternativeAddresses, x => x.Address == "Doornepol 11, 5301 LV Zaltbommel");
    }

    [Fact]
    public async Task GetDepotsAsyncFallsBackToGeocodeOverrideWhenNoTripEvidenceExists()
    {
        var service = CreateService(
            fleetRows:
            [
                new("Depot Missing", "Pakketten", "9", "DD-44-DD")
            ],
            stopRows: [],
            geocodeOverrides:
            [
                "Depot Missing,52.12,5.34,test-override"
            ]);

        var response = await service.GetDepotsAsync(CancellationToken.None);

        var depot = Assert.Single(response.Depots);
        Assert.Equal("", depot.Address);
        Assert.Equal(52.12, depot.Lat);
        Assert.Equal(5.34, depot.Lon);
        Assert.Equal("review", depot.MatchStatus);
        Assert.Equal(0, depot.MatchConfidencePct);
        Assert.Equal("test-override", depot.GeocodeSource);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FleetDataService CreateService(
        IReadOnlyList<FleetRow> fleetRows,
        IReadOnlyList<StopRow> stopRows,
        IReadOnlyList<string> geocodeOverrides)
    {
        var cacheDir = Path.Combine(_root, ".cache");
        Directory.CreateDirectory(cacheDir);
        WriteStopsParquet(Path.Combine(cacheDir, "route_stops_Test.parquet"), stopRows);
        var fleetPath = Path.Combine(_root, "fleet.xlsx");
        WriteFleetWorkbook(fleetPath, fleetRows);
        var overridePath = Path.Combine(_root, "fleet_geocoded.csv");
        File.WriteAllLines(overridePath, ["name,lat,lon,source", .. geocodeOverrides]);

        var options = new RouteAnalysisOptions
        {
            RepoRoot = _root,
            CacheDir = cacheDir,
            UploadedDatasetDir = Path.Combine(cacheDir, "uploaded-dataset", "active"),
            DuckDbPath = Path.Combine(cacheDir, "planner", "route-analysis.duckdb"),
            ManifestPath = Path.Combine(cacheDir, "planner", "manifest.json"),
            FleetExcelPath = fleetPath,
            GeocodingOverridePath = overridePath
        };

        return new FleetDataService(
            options,
            new DuckDbRouteStore(options),
            new StaticHttpClientFactory(new HttpClient(new ForbiddenHandler())),
            NullLogger<FleetDataService>.Instance);
    }

    private static StopRow Stop(string code, string tripId, int stopSeq, string address, double lat, double lon)
    {
        return new StopRow(code, tripId, stopSeq, address, lat, lon);
    }

    private static void WriteStopsParquet(string path, IReadOnlyList<StopRow> rows)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection,
            """
            CREATE TABLE stops(
                wagencode VARCHAR,
                vervoerder VARCHAR,
                vervoerder_type VARCHAR,
                trip_date DATE,
                trip_id VARCHAR,
                stop_seq BIGINT,
                acties VARCHAR,
                locatie_naam VARCHAR,
                adres VARCHAR,
                gepland_start TIMESTAMP,
                gepland_eind TIMESTAMP,
                afstand_km DOUBLE,
                afstand_km_trip DOUBLE,
                dwell_min DOUBLE,
                lat DOUBLE,
                lon DOUBLE
            );
            """);

        foreach (var row in rows)
        {
            Execute(connection, $"""
                INSERT INTO stops VALUES (
                    '{Sql(row.Code)}',
                    'Eigen vervoer',
                    'eigen',
                    DATE '2026-01-01',
                    '{Sql(row.TripId)}',
                    {row.StopSeq},
                    'Stop',
                    '{Sql(row.Address)}',
                    '{Sql(row.Address)}',
                    TIMESTAMP '2026-01-01 08:00:00' + INTERVAL {row.StopSeq} HOUR,
                    TIMESTAMP '2026-01-01 08:00:00' + INTERVAL {row.StopSeq} HOUR,
                    0.0,
                    100.0,
                    0.0,
                    {row.Lat.ToString(CultureInfo.InvariantCulture)},
                    {row.Lon.ToString(CultureInfo.InvariantCulture)}
                );
                """);
        }

        Execute(connection, $"COPY stops TO '{Sql(path)}' (FORMAT PARQUET);");
    }

    private static void WriteFleetWorkbook(string path, IReadOnlyList<FleetRow> rows)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        AddEntry(archive, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        AddEntry(archive, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        AddEntry(archive, "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Alle wagens" sheetId="1" r:id="rId1"/>
                <sheet name="Aantal per standplaats" sheetId="2" r:id="rId2"/>
              </sheets>
            </workbook>
            """);
        AddEntry(archive, "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
            </Relationships>
            """);

        AddEntry(archive, "xl/worksheets/sheet1.xml", Worksheet(
        [
            ["Vlootnummer", "Kenteken", "Regio", "Opstapplaats", "Type locatie", "Merk", "Soort voertuig", "Soort brandstof"],
            .. rows.Select(r => new[] { r.Code, r.Kenteken, "", r.Depot, r.TypeLocatie, "Merk", "Trekker", "Elektrisch" })
        ]));
        AddEntry(archive, "xl/worksheets/sheet2.xml", Worksheet(
        [
            ["Opstapplaats", "Type locatie", "Aantal wagens"],
            .. rows.GroupBy(r => r.Depot).Select(g => new[] { g.Key, g.First().TypeLocatie, g.Count().ToString(CultureInfo.InvariantCulture) })
        ]));
    }

    private static string Worksheet(IEnumerable<IReadOnlyList<string>> rows)
    {
        var xmlRows = rows.Select((row, rowIdx) =>
        {
            var cells = row.Select((value, colIdx) =>
                $"""<c r="{ColumnName(colIdx)}{rowIdx + 1}" t="inlineStr"><is><t>{WebUtility.HtmlEncode(value)}</t></is></c>""");
            return $"<row r=\"{rowIdx + 1}\">{string.Concat(cells)}</row>";
        });
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                {string.Concat(xmlRows)}
              </sheetData>
            </worksheet>
            """;
    }

    private static string ColumnName(int index)
    {
        var dividend = index + 1;
        var name = "";
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            name = (char)('A' + modulo) + name;
            dividend = (dividend - modulo) / 26;
        }

        return name;
    }

    private static void AddEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record FleetRow(string Depot, string TypeLocatie, string Code, string Kenteken);

    private sealed record StopRow(string Code, string TripId, int StopSeq, string Address, double Lat, double Lon);

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }
}
