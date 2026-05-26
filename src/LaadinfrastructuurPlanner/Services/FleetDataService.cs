using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using DuckDB.NET.Data;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed class FleetDataService
{
    private const string DepotsSheet = "Aantal per standplaats";
    private const string VehiclesSheet = "Alle wagens";
    private const string GeocodeCacheFile = "fleet_geocode.json";
    private const string UserAgent = "LaadinfrastructuurPlanner/1.0 (info@nijenhuistrucksolutions.nl)";
    private const string Disclaimer =
        "Standplaatsen zijn ruw gegeocodeerd op basis van plaatsnaam (zonder exact adres). "
        + "Markers kunnen afwijken van de werkelijke depotlocatie. Vervang later door exacte adressen.";

    private readonly RouteAnalysisOptions _options;
    private readonly DuckDbRouteStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FleetDataService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _geocodeLock = new(1, 1);

    private FleetData? _data;
    private Dictionary<string, GeocodeEntry> _geocodeCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _geocodeLoaded;

    public FleetDataService(
        RouteAnalysisOptions options,
        DuckDbRouteStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<FleetDataService> logger)
    {
        _options = options;
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FleetDepotsResponse> GetDepotsAsync(CancellationToken cancellationToken)
    {
        var data = await EnsureLoadedAsync(cancellationToken);
        if (data is null)
        {
            return new FleetDepotsResponse(
                "missing",
                "Wagenpark-Excel niet gevonden op de verwachte locatie.",
                _options.FleetExcelPath ?? "(niet geconfigureerd)",
                Disclaimer,
                0,
                []);
        }

        var geocode = await EnsureGeocodingAsync(data.Depots.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);
        var tripStats = await ComputeTripStatsAsync(data.Vehicles, cancellationToken);

        var depots = data.Depots
            .Select(depot =>
            {
                var vehiclesAtDepot = data.Vehicles
                    .Where(v => string.Equals(v.Opstapplaats, depot.Name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var enriched = vehiclesAtDepot
                    .Select(v =>
                    {
                        var stats = tripStats.GetValueOrDefault(v.Vlootnummer);
                        return new FleetVehicle(
                            v.Vlootnummer,
                            v.Kenteken,
                            v.KentekenNorm,
                            v.Regio,
                            v.Opstapplaats,
                            v.TypeLocatie,
                            v.Merk,
                            v.SoortVoertuig,
                            v.SoortBrandstof,
                            stats?.Trips ?? 0,
                            stats?.Km ?? 0);
                    })
                    .OrderByDescending(v => v.TripsInData)
                    .ThenBy(v => v.Kenteken, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var matched = enriched.Count(v => v.TripsInData > 0);
                geocode.TryGetValue(depot.Name, out var location);
                return new FleetDepot(
                    DepotId: "fleet:" + depot.Name,
                    Name: depot.Name,
                    Regio: depot.Regio,
                    TypeLocatie: depot.TypeLocatie,
                    Lat: location?.Lat ?? 0,
                    Lon: location?.Lon ?? 0,
                    GeocodeQuery: location?.Query ?? "",
                    GeocodeSource: location?.Source ?? "missing",
                    Vehicles: enriched.Length,
                    MatchedInTrips: matched,
                    VehicleList: enriched);
            })
            .Where(d => d.Lat != 0 && d.Lon != 0)
            .OrderByDescending(d => d.Vehicles)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FleetDepotsResponse(
            "ok",
            null,
            data.SourceLabel,
            Disclaimer,
            data.Vehicles.Count,
            depots);
    }

    private async Task<FleetData?> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_data is not null)
        {
            return _data;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_data is not null)
            {
                return _data;
            }

            var path = _options.FleetExcelPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            _data = LoadFromExcel(path);
            return _data;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static FleetData LoadFromExcel(string path)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sharedStrings = XlsxReader.ReadSharedStrings(archive);

        var depotsPath = XlsxReader.ResolveWorksheetPath(archive, DepotsSheet);
        var vehiclesPath = XlsxReader.ResolveWorksheetPath(archive, VehiclesSheet);
        if (depotsPath is null || vehiclesPath is null)
        {
            throw new InvalidOperationException($"Wagenpark-Excel mist sheet '{DepotsSheet}' of '{VehiclesSheet}'.");
        }

        var depotRows = XlsxReader.ReadWorksheetRows(archive, depotsPath, sharedStrings).ToArray();
        var vehicleRows = XlsxReader.ReadWorksheetRows(archive, vehiclesPath, sharedStrings).ToArray();

        var depots = ParseDepots(depotRows);
        var vehicles = ParseVehicles(vehicleRows);
        return new FleetData(depots, vehicles, Path.GetFileName(path));
    }

    private static List<FleetDepotRaw> ParseDepots(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var headers = rows[0].Select(XlsxReader.NormalizeHeader).ToArray();
        var nameIdx = FindHeader(headers, "opstapplaats", "opstapplaats:");
        var typeIdx = FindHeader(headers, "type_locatie", "type_locatie:");
        if (nameIdx < 0)
        {
            return [];
        }

        var list = new List<FleetDepotRaw>();
        foreach (var row in rows.Skip(1))
        {
            var name = XlsxReader.Cell(row, nameIdx).Trim();
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "Totaal", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var typeLocatie = typeIdx >= 0 ? XlsxReader.Cell(row, typeIdx).Trim() : "";
            if (list.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            list.Add(new FleetDepotRaw(name, "", typeLocatie));
        }

        return list;
    }

    private static List<FleetVehicleRaw> ParseVehicles(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var headers = rows[0].Select(XlsxReader.NormalizeHeader).ToArray();
        var vlootIdx = FindHeader(headers, "vloot-______nummer", "vlootnummer", "vloot-nummer", "vloot-______nummer:", "vloot-_nummer", "vloot");
        var kentekenIdx = FindHeader(headers, "kenteken", "kenteken:");
        var regioIdx = FindHeader(headers, "regio", "regio:");
        var opstapIdx = FindHeader(headers, "opstapplaats", "opstapplaats:");
        var typeLocIdx = FindHeader(headers, "type_locatie", "type___________________locatie:", "type_locatie:");
        var merkIdx = FindHeader(headers, "merk", "merk:");
        var soortIdx = FindHeader(headers, "soort_voertuig", "soort____________voertuig:", "soort_voertuig:");
        var brandstofIdx = FindHeader(headers, "soort_brandstof", "soort_____________________brandstof:", "soort_brandstof:");

        if (kentekenIdx < 0 || opstapIdx < 0)
        {
            return [];
        }

        var list = new List<FleetVehicleRaw>();
        foreach (var row in rows.Skip(1))
        {
            var kenteken = XlsxReader.Cell(row, kentekenIdx).Trim();
            var depot = XlsxReader.Cell(row, opstapIdx).Trim();
            if (string.IsNullOrWhiteSpace(kenteken) || string.IsNullOrWhiteSpace(depot))
            {
                continue;
            }

            list.Add(new FleetVehicleRaw(
                Vlootnummer: vlootIdx >= 0 ? XlsxReader.Cell(row, vlootIdx).Trim() : "",
                Kenteken: kenteken,
                KentekenNorm: NormalizeKenteken(kenteken),
                Regio: regioIdx >= 0 ? XlsxReader.Cell(row, regioIdx).Trim() : "",
                Opstapplaats: depot,
                TypeLocatie: typeLocIdx >= 0 ? XlsxReader.Cell(row, typeLocIdx).Trim() : "",
                Merk: merkIdx >= 0 ? XlsxReader.Cell(row, merkIdx).Trim() : "",
                SoortVoertuig: soortIdx >= 0 ? XlsxReader.Cell(row, soortIdx).Trim() : "",
                SoortBrandstof: brandstofIdx >= 0 ? XlsxReader.Cell(row, brandstofIdx).Trim() : ""));
        }

        return list;
    }

    private static int FindHeader(string[] headers, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = XlsxReader.NormalizeHeader(candidate);
            var idx = Array.FindIndex(headers, h => h.Contains(normalized, StringComparison.Ordinal));
            if (idx >= 0)
            {
                return idx;
            }
        }

        return -1;
    }

    private static string NormalizeKenteken(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private async Task<Dictionary<string, GeocodeEntry>> EnsureGeocodingAsync(IEnumerable<string> depotNames, CancellationToken cancellationToken)
    {
        await _geocodeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_geocodeLoaded)
            {
                _geocodeCache = LoadGeocodeCache(GeocodeCachePath());
                ApplyManualOverrides(_geocodeCache, _options.GeocodingOverridePath);
                _geocodeLoaded = true;
            }

            var missing = depotNames.Where(name => !_geocodeCache.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length == 0)
            {
                return _geocodeCache;
            }

            var client = _httpClientFactory.CreateClient(nameof(FleetDataService));
            foreach (var name in missing)
            {
                var entry = await GeocodeAsync(client, name, cancellationToken);
                if (entry is not null)
                {
                    _geocodeCache[name] = entry;
                }
                else
                {
                    _logger.LogWarning("Geocoding gefaald voor standplaats {Name}", name);
                }

                await Task.Delay(TimeSpan.FromSeconds(1.1), cancellationToken);
            }

            SaveGeocodeCache(GeocodeCachePath(), _geocodeCache);
            return _geocodeCache;
        }
        finally
        {
            _geocodeLock.Release();
        }
    }

    private async Task<GeocodeEntry?> GeocodeAsync(HttpClient client, string depotName, CancellationToken cancellationToken)
    {
        var queries = BuildGeocodeQueries(depotName);
        foreach (var query in queries)
        {
            try
            {
                var url = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=nl&q={Uri.EscapeDataString(query)}";
                using var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    continue;
                }

                var first = doc.RootElement[0];
                if (!first.TryGetProperty("lat", out var latEl) || !first.TryGetProperty("lon", out var lonEl))
                {
                    continue;
                }

                if (!double.TryParse(latEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    || !double.TryParse(lonEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    continue;
                }

                return new GeocodeEntry(lat, lon, query, "nominatim");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Geocoding fout voor {Query}", query);
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildGeocodeQueries(string depotName)
    {
        var trimmed = depotName.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        var spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx > 0 && spaceIdx < trimmed.Length - 1)
        {
            var rest = trimmed[(spaceIdx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(rest))
            {
                yield return rest + ", Nederland";
            }
        }

        yield return trimmed + ", Nederland";
    }

    private string GeocodeCachePath()
    {
        var dir = Path.GetDirectoryName(_options.DuckDbPath)!;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, GeocodeCacheFile);
    }

    private static Dictionary<string, GeocodeEntry> LoadGeocodeCache(string path)
    {
        if (!File.Exists(path))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, GeocodeEntry>>(json);
            return raw is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GeocodeEntry>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveGeocodeCache(string path, Dictionary<string, GeocodeEntry> cache)
    {
        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static void ApplyManualOverrides(Dictionary<string, GeocodeEntry> cache, string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath) || !File.Exists(overridePath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadAllLines(overridePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                {
                    continue;
                }
                var parts = line.Split(',');
                if (parts.Length < 3)
                {
                    continue;
                }
                var name = parts[0].Trim().Trim('"');
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    || !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    continue;
                }
                var source = parts.Length > 3 ? parts[3].Trim().Trim('"') : "manual";
                cache[name] = new GeocodeEntry(lat, lon, name + " (override)", source);
            }
        }
        catch
        {
        }
    }

    private async Task<Dictionary<string, TripStat>> ComputeTripStatsAsync(IReadOnlyList<FleetVehicleRaw> vehicles, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, TripStat>(StringComparer.OrdinalIgnoreCase);
        if (vehicles.Count == 0)
        {
            return result;
        }

        try
        {
            await _store.EnsureReadyAsync(cancellationToken);
            await using var connection = _store.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            if (!HasStopsTable(connection))
            {
                return result;
            }

            var hasTripDistance = HasColumn(connection, "stops", "afstand_km_trip");
            var hasSegmentDistance = HasColumn(connection, "stops", "afstand_km");
            string tripKmExpr;
            if (hasTripDistance && hasSegmentDistance)
            {
                tripKmExpr = "CASE WHEN MAX(afstand_km_trip) > 0 THEN MAX(afstand_km_trip) ELSE SUM(COALESCE(afstand_km, 0)) END";
            }
            else if (hasTripDistance)
            {
                tripKmExpr = "MAX(COALESCE(afstand_km_trip, 0))";
            }
            else if (hasSegmentDistance)
            {
                tripKmExpr = "SUM(COALESCE(afstand_km, 0))";
            }
            else
            {
                tripKmExpr = "0";
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
                WITH per_trip AS (
                    SELECT trim(wagencode) AS code,
                           trip_id,
                           {tripKmExpr} AS trip_km
                    FROM stops
                    WHERE wagencode IS NOT NULL AND trim(wagencode) <> ''
                    GROUP BY trim(wagencode), trip_id
                )
                SELECT code,
                       COUNT(*) AS trips,
                       COALESCE(SUM(trip_km), 0) AS km
                FROM per_trip
                GROUP BY code";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var code = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (code.Length == 0)
                {
                    continue;
                }
                var trips = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
                var km = reader.IsDBNull(2) ? 0.0 : Convert.ToDouble(reader.GetValue(2));
                result[code] = new TripStat(trips, km);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trip-stats voor wagenpark mislukt; voertuigen tonen zonder kilometers.");
        }

        return result;
    }

    private static bool HasStopsTable(DuckDBConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_name = 'stops'";
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static bool HasColumn(DuckDBConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = $table AND column_name = $column";
        var tableParam = cmd.CreateParameter();
        tableParam.ParameterName = "table";
        tableParam.Value = table;
        cmd.Parameters.Add(tableParam);
        var columnParam = cmd.CreateParameter();
        columnParam.ParameterName = "column";
        columnParam.Value = column;
        cmd.Parameters.Add(columnParam);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private sealed record FleetData(List<FleetDepotRaw> Depots, List<FleetVehicleRaw> Vehicles, string SourceLabel);

    private sealed record FleetDepotRaw(string Name, string Regio, string TypeLocatie);

    private sealed record FleetVehicleRaw(
        string Vlootnummer,
        string Kenteken,
        string KentekenNorm,
        string Regio,
        string Opstapplaats,
        string TypeLocatie,
        string Merk,
        string SoortVoertuig,
        string SoortBrandstof);

    private sealed record GeocodeEntry(double Lat, double Lon, string Query, string Source);

    private sealed record TripStat(long Trips, double Km);
}
