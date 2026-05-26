using System.Data.Common;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    public async Task<DataQualityReport> GetDataQualityReportAsync(CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops)
        {
            return new DataQualityReport(
                "missing",
                "Geen rittendata geladen.",
                0, 0, 0, null, null, [],
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        using var connection = OpenConnection();
        var totals = await QuerySingleAsync(
            connection,
            """
            SELECT
                COUNT(*) AS rows,
                COUNT(DISTINCT wagencode) AS vehicles,
                COUNT(DISTINCT trip_id) AS trips,
                MIN(trip_date) AS first_date,
                MAX(trip_date) AS last_date
            FROM stops;
            """,
            r => (
                Rows: GetInt64(r, "rows"),
                Vehicles: GetInt64(r, "vehicles"),
                Trips: GetInt64(r, "trips"),
                First: GetDateOnly(r, "first_date"),
                Last: GetDateOnly(r, "last_date")),
            cancellationToken);

        var totalRows = Math.Max(1, totals.Rows);
        var issues = new List<DataQualityIssue>();

        async Task AddCountAsync(string code, string description, string sql, string severity)
        {
            var count = await ScalarLongAsync(connection, sql, cancellationToken);
            issues.Add(new DataQualityIssue(
                code,
                description,
                count,
                Math.Round(100.0 * count / totalRows, 3),
                severity));
        }

        await AddCountAsync(
            "missing_coordinates",
            "Stops zonder lat/lon",
            "SELECT COUNT(*) FROM stops WHERE lat IS NULL OR lon IS NULL;",
            "warning");

        await AddCountAsync(
            "missing_plate",
            "Stops zonder kenteken_norm",
            "SELECT COUNT(*) FROM stops WHERE kenteken_norm IS NULL OR trim(kenteken_norm) = '';",
            "warning");

        await AddCountAsync(
            "time_inversion",
            "Stops waarbij eindtijd <= starttijd",
            "SELECT COUNT(*) FROM stops WHERE gepland_start IS NOT NULL AND gepland_eind IS NOT NULL AND gepland_eind <= gepland_start;",
            "error");

        await AddCountAsync(
            "zero_distance_trips",
            "Stops met afstand_km <= 0",
            "SELECT COUNT(*) FROM stops WHERE COALESCE(afstand_km, 0) <= 0 AND COALESCE(afstand_km_trip, 0) <= 0;",
            "info");

        await AddCountAsync(
            "negative_distance",
            "Stops met negatieve afstand",
            "SELECT COUNT(*) FROM stops WHERE COALESCE(afstand_km, 0) < 0 OR COALESCE(afstand_km_trip, 0) < 0;",
            "error");

        await AddCountAsync(
            "implausible_speed",
            "Trips met gemiddelde snelheid > 120 km/h",
            """
            SELECT COUNT(*) FROM (
                SELECT wagencode, trip_id,
                       MAX(afstand_km_trip) AS km,
                       date_diff('second', MIN(gepland_start), MAX(gepland_eind)) / 3600.0 AS hours
                FROM stops
                WHERE gepland_start IS NOT NULL AND gepland_eind IS NOT NULL
                GROUP BY wagencode, trip_id
            ) t
            WHERE hours > 0 AND km / hours > 120;
            """,
            "warning");

        await AddCountAsync(
            "duplicate_trip_id",
            "Trip-ids die meermaals voorkomen voor zelfde voertuig + zelfde datum",
            """
            SELECT COALESCE(SUM(extra), 0) FROM (
                SELECT wagencode, trip_date, trip_id, COUNT(*) - 1 AS extra
                FROM (SELECT DISTINCT wagencode, trip_date, trip_id, stop_seq FROM stops) s
                GROUP BY wagencode, trip_date, trip_id
                HAVING COUNT(*) - 1 > 0
            ) d;
            """,
            "info");

        await AddCountAsync(
            "long_dwell_outlier",
            "Stops met dwell_min > 48h (mogelijk overgang weekend/storing)",
            "SELECT COUNT(*) FROM stops WHERE COALESCE(dwell_min, 0) > 48 * 60;",
            "info");

        return new DataQualityReport(
            "ok",
            null,
            totals.Rows,
            totals.Vehicles,
            totals.Trips,
            totals.First,
            totals.Last,
            issues.ToArray(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
