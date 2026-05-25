using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    public async Task<CorridorHotspot[]> GetCorridorHotspotsAsync(
        AnalysisFilter filter,
        ChargingScenario? scenarioOverride = null,
        int topN = 25,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("road_selection_index"))
        {
            return [];
        }

        var scenario = NormalizeScenario(scenarioOverride ?? new ChargingScenario());
        var normalized = NormalizeFilter(filter);
        var where = new List<string> { "distance_km > 0" };
        if (normalized.DateFrom is not null)
        {
            where.Add($"trip_date >= DATE {DuckDbRouteStore.SqlString(normalized.DateFrom.Value.ToString("yyyy-MM-dd"))}");
        }
        if (normalized.DateTo is not null)
        {
            where.Add($"trip_date <= DATE {DuckDbRouteStore.SqlString(normalized.DateTo.Value.ToString("yyyy-MM-dd"))}");
        }
        var whereClause = string.Join(" AND ", where);

        using var connection = OpenConnection();
        var rows = await QueryListAsync(
            connection,
            $$"""
            WITH segments AS (
                SELECT
                    printf('%.2f:%.2f', ROUND(mid_lat, 2), ROUND(mid_lon, 2)) AS segment_id,
                    ROUND(mid_lat, 2) AS lat,
                    ROUND(mid_lon, 2) AS lon,
                    distance_km,
                    wagencode,
                    trip_id
                FROM road_selection_index
                WHERE {{whereClause}}
            )
            SELECT
                segment_id,
                AVG(lat) AS lat,
                AVG(lon) AS lon,
                COUNT(DISTINCT (wagencode, trip_id)) AS trips,
                COUNT(DISTINCT wagencode) AS unique_vehicles,
                SUM(distance_km) AS total_km
            FROM segments
            GROUP BY segment_id
            ORDER BY trips DESC
            LIMIT {{Math.Clamp(topN, 1, 200)}};
            """,
            r => new
            {
                SegmentId = GetString(r, "segment_id"),
                Lat = GetDouble(r, "lat"),
                Lon = GetDouble(r, "lon"),
                Trips = GetInt64(r, "trips"),
                UniqueVehicles = GetInt64(r, "unique_vehicles"),
                TotalKm = GetDouble(r, "total_km"),
            },
            cancellationToken);

        var usable = scenario.CapacityKwh * Math.Max(0, scenario.TargetSocPct - scenario.MinSocPct) / 100.0;
        return rows.Select(r =>
        {
            var demandKwh = r.TotalKm * scenario.KwhPerKm;
            var corridorMwh = Math.Max(0, demandKwh - usable * r.Trips) / 1000.0;
            return new CorridorHotspot(
                SegmentId: r.SegmentId,
                Lat: Math.Round(r.Lat, 4),
                Lon: Math.Round(r.Lon, 4),
                Description: $"Wegvlak rond {r.Lat:0.00}, {r.Lon:0.00}",
                Trips: r.Trips,
                UniqueVehicles: r.UniqueVehicles,
                TotalCorridorMwh: Math.Round(corridorMwh, 2),
                NearestPublicChargerKm: 0);
        }).ToArray();
    }
}
