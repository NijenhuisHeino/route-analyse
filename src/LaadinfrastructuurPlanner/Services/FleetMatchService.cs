using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    public async Task<FleetMatchReport> GetFleetMatchReportAsync(CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops)
        {
            return new FleetMatchReport("missing", "Geen rittendata geladen.", 0, 0, 0, []);
        }

        var fleetPath = _store.Options.FleetExcelPath;
        if (string.IsNullOrWhiteSpace(fleetPath) || !File.Exists(fleetPath))
        {
            return new FleetMatchReport("missing", "Wagenpark-Excel niet gevonden.", 0, 0, 0, []);
        }

        var fleetKeys = LoadFleetVehicleKeys(fleetPath);
        if (fleetKeys.Count == 0)
        {
            return new FleetMatchReport("missing", "Wagenpark-Excel bevat geen voertuigen.", 0, 0, 0, []);
        }

        using var connection = OpenConnection();
        var totalVehicles = await ScalarLongAsync(
            connection,
            "SELECT COUNT(DISTINCT COALESCE(NULLIF(kenteken_norm, ''), wagencode)) FROM stops;",
            cancellationToken);

        var unknown = await QueryListAsync(
            connection,
            $$"""
            SELECT
                COALESCE(NULLIF(kenteken_norm, ''), wagencode) AS vehicle_key,
                COALESCE(NULLIF(kenteken, ''), '') AS kenteken,
                COALESCE(NULLIF(wagencode, ''), '') AS wagencode,
                COUNT(*) AS stops,
                COUNT(DISTINCT trip_id) AS trips,
                MIN(trip_date) AS first_date,
                MAX(trip_date) AS last_date
            FROM stops
            WHERE COALESCE(NULLIF(kenteken_norm, ''), wagencode) NOT IN ({{string.Join(", ", fleetKeys.Select(DuckDbRouteStore.SqlString))}})
            GROUP BY 1, 2, 3
            ORDER BY trips DESC, stops DESC
            LIMIT 5000;
            """,
            r => new FleetUnknownVehicle(
                GetString(r, "vehicle_key"),
                GetString(r, "kenteken"),
                GetString(r, "wagencode"),
                GetInt64(r, "stops"),
                GetInt64(r, "trips"),
                GetDateOnly(r, "first_date"),
                GetDateOnly(r, "last_date")),
            cancellationToken);

        var matched = totalVehicles - unknown.Count;
        return new FleetMatchReport(
            "ok",
            null,
            fleetKeys.Count,
            totalVehicles,
            matched,
            unknown.ToArray());
    }

    private static HashSet<string> LoadFleetVehicleKeys(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
            var sharedStrings = XlsxReader.ReadSharedStrings(archive);
            var vehiclesPath = XlsxReader.ResolveWorksheetPath(archive, "Alle wagens");
            if (vehiclesPath is null)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }

            var rows = XlsxReader.ReadWorksheetRows(archive, vehiclesPath, sharedStrings).ToArray();
            if (rows.Length == 0)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }

            var headers = rows[0].Select(XlsxReader.NormalizeHeader).ToArray();
            var vlootIdx = Array.FindIndex(headers, h => h.Contains("vloot", StringComparison.Ordinal));
            var kentekenIdx = Array.FindIndex(headers, h => h.Contains("kenteken", StringComparison.Ordinal));
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Skip(1))
            {
                if (vlootIdx >= 0)
                {
                    var vloot = XlsxReader.Cell(row, vlootIdx).Trim();
                    if (!string.IsNullOrWhiteSpace(vloot))
                    {
                        keys.Add(vloot);
                    }
                }
                if (kentekenIdx >= 0)
                {
                    var plate = new string(XlsxReader.Cell(row, kentekenIdx).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
                    if (!string.IsNullOrWhiteSpace(plate))
                    {
                        keys.Add(plate);
                    }
                }
            }

            return keys;
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

public sealed record FleetUnknownVehicle(
    string VehicleKey,
    string Kenteken,
    string Wagencode,
    long Stops,
    long Trips,
    DateOnly? FirstDate,
    DateOnly? LastDate);

public sealed record FleetMatchReport(
    string Status,
    string? Message,
    long FleetVehicleCount,
    long TripDataVehicleCount,
    long MatchedCount,
    FleetUnknownVehicle[] UnknownVehicles);
