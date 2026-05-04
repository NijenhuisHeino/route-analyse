using System.Data.Common;
using System.Globalization;
using Postnl.LaadinfrastructuurPlanner.Models;

namespace Postnl.LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    private const int DistanceBucketKm = 50;

    public async Task<OvernightLocationsResponse> GetOvernightLocationsAsync(
        OvernightLocationsRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("overnight_events"))
        {
            return new OvernightLocationsResponse("cache_missing", "Overnachtlocaties zijn nog niet beschikbaar.", [], false);
        }

        var normalized = NormalizeOvernightRequest(request);
        var scenario = NormalizeScenario(normalized.Scenario);
        var minVehicles = Math.Clamp(normalized.MinVehicles, 1, 1000);
        var limit = Math.Clamp(normalized.MarkerTopN, 25, 500);
        var key = CacheKey("overnight-locations", new { normalized, scenario, minVehicles, limit });

        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildOvernightWhere(normalized);
            var nearestSql = _store.HasView("chargers")
                ? $$"""
                  COALESCE((
                    SELECT MIN({{HaversineSql("g.lat", "g.lon", "CAST(c.lat AS DOUBLE)", "CAST(c.lon AS DOUBLE)")}})
                    FROM chargers c
                    WHERE c.lat IS NOT NULL
                        AND c.lon IS NOT NULL
                        AND COALESCE(CAST(c.toegankelijkheid AS VARCHAR), '') IN ('Publiek', 'Semi-publiek')
                  ), -1)
                  """
                : "-1";

            var locations = await QueryListAsync(
                connection,
                $$"""
                WITH filtered AS (
                    SELECT *
                    FROM overnight_events
                    WHERE {{where}}
                ),
                load AS (
                    SELECT
                        depot_id,
                        start_lat,
                        start_lon,
                        start_address,
                        wagencode,
                        gap_hours,
                        day_km,
                        day_km * {{SqlDouble(scenario.KwhPerKm)}} AS demand_kwh,
                        LEAST(day_km * {{SqlDouble(scenario.KwhPerKm)}}, {{SqlDouble(UsableKwh(scenario))}}) AS local_demand_kwh,
                        GREATEST(day_km * {{SqlDouble(scenario.KwhPerKm)}} - {{SqlDouble(UsableKwh(scenario))}}, 0) AS corridor_demand_kwh
                    FROM filtered
                ),
                event_load AS (
                    SELECT
                        *,
                        LEAST(
                            local_demand_kwh,
                            gap_hours * {{SqlDouble(scenario.KwPerPlug)}} * {{scenario.Plugs}},
                            gap_hours * {{SqlDouble(scenario.SiteLimitMw)}} * 1000.0
                        ) AS deliverable_kwh
                    FROM load
                ),
                grouped AS (
                    SELECT
                        depot_id,
                        AVG(start_lat) AS lat,
                        AVG(start_lon) AS lon,
                        COALESCE(MODE(start_address), '') AS address,
                        COUNT(*) AS events,
                        COUNT(DISTINCT wagencode) AS unique_vehicles,
                        COALESCE(quantile_cont(gap_hours, 0.5), 0) AS median_gap_hours,
                        COALESCE(quantile_cont(day_km, 0.95), 0) AS p95_day_km,
                        COALESCE(SUM(demand_kwh), 0) / 1000.0 AS total_mwh,
                        COALESCE(SUM(corridor_demand_kwh + GREATEST(local_demand_kwh - deliverable_kwh, 0)), 0) / 1000.0 AS shortage_mwh
                    FROM event_load
                    GROUP BY depot_id
                )
                SELECT
                    depot_id,
                    lat,
                    lon,
                    address,
                    CAST(events AS BIGINT) AS events,
                    CAST(unique_vehicles AS BIGINT) AS unique_vehicles,
                    median_gap_hours,
                    p95_day_km,
                    total_mwh,
                    shortage_mwh,
                    {{nearestSql}} AS nearest_charger_km
                FROM grouped g
                WHERE unique_vehicles >= {{minVehicles}}
                ORDER BY shortage_mwh DESC, total_mwh DESC, unique_vehicles DESC
                LIMIT {{limit}};
                """,
                r =>
                {
                    var totalMwh = GetDouble(r, "total_mwh");
                    var shortageMwh = GetDouble(r, "shortage_mwh");
                    return new OvernightLocationMarker(
                        GetString(r, "depot_id"),
                        Math.Round(GetDouble(r, "lat"), 6),
                        Math.Round(GetDouble(r, "lon"), 6),
                        GetString(r, "address"),
                        GetInt64(r, "unique_vehicles"),
                        GetInt64(r, "events"),
                        Math.Round(GetDouble(r, "median_gap_hours"), 1),
                        Math.Round(GetDouble(r, "p95_day_km"), 1),
                        Math.Round(totalMwh, 1),
                        Math.Round(shortageMwh, 1),
                        Math.Round(GetDouble(r, "nearest_charger_km"), 1),
                        Recommend(totalMwh, shortageMwh));
                },
                cancellationToken);

            return new OvernightLocationsResponse("ok", null, locations.ToArray(), true);
        });
    }

    public async Task<SelectionDetailResponse> GetOvernightLocationDetailAsync(
        OvernightLocationDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("overnight_events"))
        {
            return EmptySelection("cache_missing", "depot", request.DepotId, "Overnachtlocaties zijn nog niet beschikbaar.", "Overnachtlocaties zijn nog niet beschikbaar.");
        }

        var normalized = NormalizeLocationDetailRequest(request);
        var scenario = NormalizeScenario(normalized.Scenario);
        var key = CacheKey("overnight-location-detail", new { normalized, scenario });
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildOvernightWhere(normalized);
            var depotId = DuckDbRouteStore.SqlString(normalized.DepotId);

            var rows = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    CAST(wagencode AS VARCHAR) AS wagencode,
                    COALESCE(CAST(kentekens AS VARCHAR), CAST(kenteken AS VARCHAR), '') AS kentekens,
                    CAST(trip_date AS DATE) AS trip_date,
                    CAST(depot_id AS VARCHAR) AS selection_id,
                    CAST(day_start AS TIMESTAMP) AS start_time,
                    CAST(day_end AS TIMESTAMP) AS end_time,
                    CAST(start_lat AS DOUBLE) AS lat,
                    CAST(start_lon AS DOUBLE) AS lon,
                    COALESCE(CAST(start_address AS VARCHAR), '') AS address,
                    COALESCE(CAST(day_km AS DOUBLE), 0) AS distance_km,
                    COALESCE(CAST(gap_hours AS DOUBLE), 0) AS gap_hours
                FROM overnight_events
                WHERE depot_id = {{depotId}}
                    AND {{where}}
                ORDER BY day_km DESC
                LIMIT 50000;
                """,
                ReadDemandEvent,
                cancellationToken);

            var heat = await QueryListAsync(
                connection,
                $$"""
                WITH selected_days AS (
                    SELECT DISTINCT wagencode, trip_date
                    FROM overnight_events
                    WHERE depot_id = {{depotId}}
                        AND {{where}}
                )
                SELECT
                    ROUND(CAST(s.lat AS DOUBLE), 3) AS lat,
                    ROUND(CAST(s.lon AS DOUBLE), 3) AS lon,
                    COUNT(*) AS weight
                FROM stops s
                JOIN selected_days d
                    ON CAST(s.wagencode AS VARCHAR) = d.wagencode
                    AND CAST(s.trip_date AS DATE) = d.trip_date
                WHERE s.lat IS NOT NULL
                    AND s.lon IS NOT NULL
                    AND NOT COALESCE(CAST(s.acties AS VARCHAR), '') ILIKE '%Administrative%'
                GROUP BY 1, 2
                ORDER BY weight DESC
                LIMIT 20000;
                """,
                r => new HeatPoint(GetDouble(r, "lat"), GetDouble(r, "lon"), GetDouble(r, "weight")),
                cancellationToken);

            var center = rows.Count > 0
                ? (rows.Average(x => x.Lat), rows.Average(x => x.Lon), rows[0].Address)
                : (0, 0, "Depot");

            return BuildSelectionDetail(
                "depot",
                normalized.DepotId,
                string.IsNullOrWhiteSpace(center.Address) ? normalized.DepotId : center.Address,
                center.Item1,
                center.Item2,
                rows,
                heat,
                scenario,
                isRoadSelection: false);
        });
    }

    public async Task<SelectionDetailResponse> GetRoadSelectionAsync(
        RoadSelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("road_selection_index"))
        {
            return EmptySelection("index_building", "road", null, "Wegvlakselectie is nog niet beschikbaar.", "Wegvlakselectie is nog niet beschikbaar.");
        }

        var normalized = NormalizeRoadSelectionRequest(request);
        var scenario = NormalizeScenario(normalized.Scenario);
        var key = CacheKey("road-selection-detail", new { normalized, scenario });
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildRoadSelectionWhere(normalized);

            var rows = await QueryListAsync(
                connection,
                $$"""
                SELECT DISTINCT
                    CAST(wagencode AS VARCHAR) AS wagencode,
                    COALESCE(CAST(kentekens AS VARCHAR), CAST(kenteken AS VARCHAR), '') AS kentekens,
                    CAST(trip_date AS DATE) AS trip_date,
                    CAST(trip_id AS VARCHAR) AS selection_id,
                    CAST(trip_start AS TIMESTAMP) AS start_time,
                    CAST(trip_end AS TIMESTAMP) AS end_time,
                    CAST(start_lat AS DOUBLE) AS lat,
                    CAST(start_lon AS DOUBLE) AS lon,
                    COALESCE(CAST(start_address AS VARCHAR), '') AS address,
                    COALESCE(CAST(distance_km AS DOUBLE), 0) AS distance_km,
                    0.0 AS gap_hours
                FROM road_selection_index
                WHERE {{where}}
                ORDER BY distance_km DESC
                LIMIT 50000;
                """,
                ReadDemandEvent,
                cancellationToken);

            var heat = await QueryListAsync(
                connection,
                $$"""
                WITH selected_trips AS (
                    SELECT DISTINCT wagencode, trip_date, trip_id
                    FROM road_selection_index
                    WHERE {{where}}
                )
                SELECT
                    ROUND(CAST(s.lat AS DOUBLE), 3) AS lat,
                    ROUND(CAST(s.lon AS DOUBLE), 3) AS lon,
                    COUNT(*) AS weight
                FROM stops s
                JOIN selected_trips t
                    ON CAST(s.wagencode AS VARCHAR) = t.wagencode
                    AND CAST(s.trip_date AS DATE) = t.trip_date
                    AND CAST(s.trip_id AS VARCHAR) = t.trip_id
                WHERE s.lat IS NOT NULL
                    AND s.lon IS NOT NULL
                    AND NOT COALESCE(CAST(s.acties AS VARCHAR), '') ILIKE '%Administrative%'
                GROUP BY 1, 2
                ORDER BY weight DESC
                LIMIT 20000;
                """,
                r => new HeatPoint(GetDouble(r, "lat"), GetDouble(r, "lon"), GetDouble(r, "weight")),
                cancellationToken);

            var centerLat = (normalized.Road.Lat1 + normalized.Road.Lat2) / 2.0;
            var centerLon = (normalized.Road.Lon1 + normalized.Road.Lon2) / 2.0;
            return BuildSelectionDetail(
                "road",
                null,
                $"Wegvlak {centerLat:0.000}, {centerLon:0.000}",
                centerLat,
                centerLon,
                rows,
                heat,
                scenario,
                isRoadSelection: true);
        });
    }

    public Task<SelectionDetailResponse> GetChargingScenarioAsync(
        ChargingScenarioRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.SelectionType, "road", StringComparison.OrdinalIgnoreCase) && request.Road is not null)
        {
            return GetRoadSelectionAsync(new RoadSelectionRequest
            {
                DateFrom = request.DateFrom,
                DateTo = request.DateTo,
                VervoerderTypes = request.VervoerderTypes,
                Vervoerders = request.Vervoerders,
                Wagencodes = request.Wagencodes,
                MinDwellMin = request.MinDwellMin,
                RoadThreshold = request.RoadThreshold,
                RoadTopPercent = request.RoadTopPercent,
                MarkerTopN = request.MarkerTopN,
                Road = request.Road,
                Scenario = request.Scenario,
            }, cancellationToken);
        }

        return GetOvernightLocationDetailAsync(new OvernightLocationDetailRequest
        {
            DateFrom = request.DateFrom,
            DateTo = request.DateTo,
            VervoerderTypes = request.VervoerderTypes,
            Vervoerders = request.Vervoerders,
            Wagencodes = request.Wagencodes,
            MinDwellMin = request.MinDwellMin,
            RoadThreshold = request.RoadThreshold,
            RoadTopPercent = request.RoadTopPercent,
            MarkerTopN = request.MarkerTopN,
            DepotId = request.DepotId ?? "",
            Scenario = request.Scenario,
        }, cancellationToken);
    }

    private SelectionDetailResponse BuildSelectionDetail(
        string selectionType,
        string? selectionId,
        string title,
        double lat,
        double lon,
        IReadOnlyList<DemandEvent> rows,
        IReadOnlyList<HeatPoint> heat,
        ChargingScenario scenario,
        bool isRoadSelection)
    {
        var distribution = BuildDistanceDistribution(rows.Select(x => x.DistanceKm));
        var charging = BuildChargingProfile(rows, scenario, isRoadSelection);
        var vehicles = rows
            .GroupBy(x => x.Wagencode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var distances = group.Select(x => x.DistanceKm).Order().ToArray();
                var kentekens = CompactLicensePlates(group.SelectMany(x => SplitLicensePlates(x.Kentekens)));
                return new SelectionVehicleRow(
                    group.Key,
                    kentekens,
                    group.LongCount(),
                    Math.Round(group.Sum(x => x.DistanceKm), 1),
                    Math.Round(QuantileSorted(distances, 0.95), 1),
                    Math.Round(group.Sum(x => x.DistanceKm * scenario.KwhPerKm) / 1000.0, 2),
                    Math.Round(group.Average(x => x.GapHours), 1));
            })
            .OrderByDescending(x => x.TotalMwh)
            .ThenByDescending(x => x.P95Km)
            .Take(100)
            .ToArray();

        var summary = new SelectionSummary(
            rows.LongCount(),
            rows.Select(x => x.Wagencode).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
            Math.Round(rows.Sum(x => x.DistanceKm), 1),
            charging.TotalMwh,
            rows.Count == 0 ? 0 : Math.Round(rows.Average(x => x.GapHours), 1),
            charging.Recommendation);

        return new SelectionDetailResponse(
            "ok",
            selectionType,
            selectionId,
            title,
            rows.Count == 0 ? "Geen ritten gevonden voor deze selectie." : null,
            Math.Round(lat, 6),
            Math.Round(lon, 6),
            summary,
            distribution,
            heat.ToArray(),
            vehicles,
            charging,
            true);
    }

    private static ChargingProfile BuildChargingProfile(
        IReadOnlyList<DemandEvent> rows,
        ChargingScenario scenario,
        bool isRoadSelection)
    {
        var usableKwh = UsableKwh(scenario);
        var eventLoads = rows.Select(row =>
        {
            var demandKwh = Math.Max(0, row.DistanceKm * scenario.KwhPerKm);
            var localDemandKwh = isRoadSelection ? 0 : Math.Min(demandKwh, usableKwh);
            var corridorDemandKwh = isRoadSelection ? demandKwh : Math.Max(0, demandKwh - usableKwh);
            var deliverableKwh = isRoadSelection
                ? 0
                : Math.Min(localDemandKwh, Math.Min(
                    row.GapHours * scenario.KwPerPlug * scenario.Plugs,
                    row.GapHours * scenario.SiteLimitMw * 1000.0));
            var shortageKwh = corridorDemandKwh + Math.Max(0, localDemandKwh - deliverableKwh);
            var requiredKw = isRoadSelection
                ? demandKwh
                : row.GapHours > 0 ? localDemandKwh / row.GapHours : localDemandKwh;

            return new
            {
                Slot = row.StartTime.ToString("HH:00", CultureInfo.InvariantCulture),
                DemandKwh = demandKwh,
                DeliverableKwh = deliverableKwh,
                ShortageKwh = shortageKwh,
                RequiredKw = requiredKw,
            };
        }).ToArray();

        var windows = eventLoads
            .GroupBy(x => x.Slot)
            .Select(group => new ChargingWindow(
                group.Key,
                group.LongCount(),
                Math.Round(group.Sum(x => x.DemandKwh), 0),
                Math.Round(group.Sum(x => x.DeliverableKwh), 0),
                Math.Round(group.Sum(x => x.ShortageKwh), 0),
                Math.Round(group.Sum(x => x.RequiredKw) / 1000.0, 2)))
            .OrderByDescending(x => x.ShortageKwh)
            .ThenByDescending(x => x.RequiredMw)
            .Take(12)
            .ToArray();

        var totalMwh = eventLoads.Sum(x => x.DemandKwh) / 1000.0;
        var shortageMwh = eventLoads.Sum(x => x.ShortageKwh) / 1000.0;
        var peakMw = windows.Length == 0 ? 0 : windows.Max(x => x.RequiredMw);
        var plugsAtPeak = scenario.KwPerPlug <= 0 ? 0 : (int)Math.Ceiling(peakMw * 1000.0 / scenario.KwPerPlug);

        return new ChargingProfile(
            rows.LongCount(),
            Math.Round(totalMwh, 1),
            Math.Round(shortageMwh, 1),
            Math.Round(peakMw, 2),
            plugsAtPeak,
            windows,
            Recommend(totalMwh, shortageMwh, isRoadSelection));
    }

    private static DistanceDistribution BuildDistanceDistribution(IEnumerable<double> distances)
    {
        var values = distances.Where(x => x >= 0).Order().ToArray();
        if (values.Length == 0)
        {
            return new DistanceDistribution(0, 0, 0, 0, 0, 0, 0, []);
        }

        var avg = values.Average();
        var variance = values.Sum(x => Math.Pow(x - avg, 2)) / values.Length;
        var maxBucket = Math.Max(DistanceBucketKm, (int)Math.Ceiling(values[^1] / DistanceBucketKm) * DistanceBucketKm);
        var buckets = Enumerable.Range(0, maxBucket / DistanceBucketKm)
            .Select(i =>
            {
                var from = i * DistanceBucketKm;
                var to = from + DistanceBucketKm;
                var count = values.LongCount(x => x >= from && (x < to || (to == maxBucket && x <= to)));
                return new DistanceBucket(from, to, count);
            })
            .Where(x => x.Trips > 0)
            .ToArray();

        return new DistanceDistribution(
            values.LongLength,
            Math.Round(avg, 1),
            Math.Round(Math.Sqrt(variance), 1),
            Math.Round(QuantileSorted(values, 0.50), 1),
            Math.Round(QuantileSorted(values, 0.75), 1),
            Math.Round(QuantileSorted(values, 0.90), 1),
            Math.Round(QuantileSorted(values, 0.95), 1),
            buckets);
    }

    private static double QuantileSorted(IReadOnlyList<double> sortedValues, double quantile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var position = (sortedValues.Count - 1) * quantile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = position - lower;
        return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
    }

    private static IEnumerable<string> SplitLicensePlates(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part.Trim();
            }
        }
    }

    private static string CompactLicensePlates(IEnumerable<string> plates)
    {
        var values = plates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (values.Length == 0)
        {
            return "-";
        }

        const int maxVisible = 4;
        var visible = values.Take(maxVisible).ToArray();
        return values.Length <= maxVisible
            ? string.Join(", ", visible)
            : string.Join(", ", visible) + $" +{values.Length - maxVisible}";
    }

    private static SelectionDetailResponse EmptySelection(
        string status,
        string selectionType,
        string? selectionId,
        string title,
        string message)
    {
        return new SelectionDetailResponse(
            status,
            selectionType,
            selectionId,
            title,
            message,
            0,
            0,
            new SelectionSummary(0, 0, 0, 0, 0, message),
            new DistanceDistribution(0, 0, 0, 0, 0, 0, 0, []),
            [],
            [],
            new ChargingProfile(0, 0, 0, 0, 0, [], message),
            false);
    }

    private OvernightLocationsRequest NormalizeOvernightRequest(OvernightLocationsRequest request)
    {
        var filter = new AnalysisFilter
        {
            DateFrom = request.DateFrom,
            DateTo = request.DateTo,
            VervoerderTypes = request.VervoerderTypes,
            Vervoerders = request.Vervoerders,
            Wagencodes = request.Wagencodes,
            MinDwellMin = request.MinDwellMin,
            RoadThreshold = request.RoadThreshold,
            RoadTopPercent = request.RoadTopPercent,
            MarkerTopN = request.MarkerTopN,
        };
        var normalized = NormalizeFilter(filter);
        return request with
        {
            DateFrom = normalized.DateFrom,
            DateTo = normalized.DateTo,
            VervoerderTypes = normalized.VervoerderTypes,
            Vervoerders = normalized.Vervoerders,
            Wagencodes = normalized.Wagencodes,
            MinDwellMin = normalized.MinDwellMin,
            RoadThreshold = normalized.RoadThreshold,
            RoadTopPercent = normalized.RoadTopPercent,
            MarkerTopN = normalized.MarkerTopN,
            MinVehicles = Math.Clamp(request.MinVehicles, 1, 1000),
            Scenario = NormalizeScenario(request.Scenario),
        };
    }

    private OvernightLocationDetailRequest NormalizeLocationDetailRequest(OvernightLocationDetailRequest request)
    {
        var normalized = NormalizeFilter(request);
        return request with
        {
            DateFrom = normalized.DateFrom,
            DateTo = normalized.DateTo,
            VervoerderTypes = normalized.VervoerderTypes,
            Vervoerders = normalized.Vervoerders,
            Wagencodes = normalized.Wagencodes,
            MinDwellMin = normalized.MinDwellMin,
            RoadThreshold = normalized.RoadThreshold,
            RoadTopPercent = normalized.RoadTopPercent,
            MarkerTopN = normalized.MarkerTopN,
            DepotId = request.DepotId.Trim(),
            Scenario = NormalizeScenario(request.Scenario),
        };
    }

    private RoadSelectionRequest NormalizeRoadSelectionRequest(RoadSelectionRequest request)
    {
        var normalized = NormalizeFilter(request);
        var road = request.Road;
        return request with
        {
            DateFrom = normalized.DateFrom,
            DateTo = normalized.DateTo,
            VervoerderTypes = normalized.VervoerderTypes,
            Vervoerders = normalized.Vervoerders,
            Wagencodes = normalized.Wagencodes,
            MinDwellMin = normalized.MinDwellMin,
            RoadThreshold = normalized.RoadThreshold,
            RoadTopPercent = normalized.RoadTopPercent,
            MarkerTopN = normalized.MarkerTopN,
            Road = road with { RadiusKm = Math.Clamp(road.RadiusKm, 0.2, 20) },
            Scenario = NormalizeScenario(request.Scenario),
        };
    }

    private static ChargingScenario NormalizeScenario(ChargingScenario scenario)
    {
        return scenario with
        {
            KwhPerKm = Math.Clamp(scenario.KwhPerKm, 0.5, 3.5),
            CapacityKwh = Math.Clamp(scenario.CapacityKwh, 100, 1_500),
            MinSocPct = Math.Clamp(scenario.MinSocPct, 0, 50),
            TargetSocPct = Math.Clamp(Math.Max(scenario.TargetSocPct, scenario.MinSocPct + 1), 20, 100),
            KwPerPlug = Math.Clamp(scenario.KwPerPlug, 50, 2_000),
            Plugs = Math.Clamp(scenario.Plugs, 1, 100),
            SiteLimitMw = Math.Clamp(scenario.SiteLimitMw, 0.05, 50),
        };
    }

    private static string BuildDailyTripWhere(AnalysisFilter filter)
    {
        var parts = new List<string> { "distance_km >= 0" };
        if (filter.DateFrom is not null)
        {
            parts.Add($"trip_date >= DATE {DuckDbRouteStore.SqlString(filter.DateFrom.Value.ToString("yyyy-MM-dd"))}");
        }

        if (filter.DateTo is not null)
        {
            parts.Add($"trip_date <= DATE {DuckDbRouteStore.SqlString(filter.DateTo.Value.ToString("yyyy-MM-dd"))}");
        }

        AddIn(parts, "vervoerder_type", filter.VervoerderTypes);
        AddIn(parts, "vervoerder", filter.Vervoerders);
        AddVehicleIn(parts, filter.Wagencodes);
        return string.Join(" AND ", parts);
    }

    private static string BuildOvernightWhere(AnalysisFilter filter)
    {
        var parts = new List<string> { "day_km >= 0" };
        if (filter.DateFrom is not null)
        {
            parts.Add($"trip_date >= DATE {DuckDbRouteStore.SqlString(filter.DateFrom.Value.ToString("yyyy-MM-dd"))}");
        }

        if (filter.DateTo is not null)
        {
            parts.Add($"trip_date <= DATE {DuckDbRouteStore.SqlString(filter.DateTo.Value.ToString("yyyy-MM-dd"))}");
        }

        AddIn(parts, "vervoerder_type", filter.VervoerderTypes);
        AddIn(parts, "vervoerder", filter.Vervoerders);
        AddVehicleIn(parts, filter.Wagencodes);
        return string.Join(" AND ", parts);
    }

    private static string BuildRoadSelectionWhere(RoadSelectionRequest request)
    {
        var road = request.Road;
        var midLat = (road.Lat1 + road.Lat2) / 2.0;
        var midLon = (road.Lon1 + road.Lon2) / 2.0;
        var radius = Math.Clamp(road.RadiusKm, 0.2, 20);
        var latDelta = radius / 111.0;
        var lonDelta = radius / Math.Max(20, 111.0 * Math.Cos(DegreesToRadians(midLat)));

        var parts = new List<string>
        {
            $"mid_lat BETWEEN {SqlDouble(midLat - latDelta)} AND {SqlDouble(midLat + latDelta)}",
            $"mid_lon BETWEEN {SqlDouble(midLon - lonDelta)} AND {SqlDouble(midLon + lonDelta)}",
            $"{HaversineSql("mid_lat", "mid_lon", SqlDouble(midLat), SqlDouble(midLon))} <= {SqlDouble(radius)}",
        };

        if (request.DateFrom is not null)
        {
            parts.Add($"trip_date >= DATE {DuckDbRouteStore.SqlString(request.DateFrom.Value.ToString("yyyy-MM-dd"))}");
        }

        if (request.DateTo is not null)
        {
            parts.Add($"trip_date <= DATE {DuckDbRouteStore.SqlString(request.DateTo.Value.ToString("yyyy-MM-dd"))}");
        }

        AddIn(parts, "vervoerder_type", request.VervoerderTypes);
        AddIn(parts, "vervoerder", request.Vervoerders);
        AddVehicleIn(parts, request.Wagencodes);
        return string.Join(" AND ", parts);
    }

    private static DemandEvent ReadDemandEvent(DbDataReader reader)
    {
        return new DemandEvent(
            GetString(reader, "wagencode"),
            GetString(reader, "kentekens"),
            GetDateOnly(reader, "trip_date") ?? DateOnly.MinValue,
            GetString(reader, "selection_id"),
            GetDateTime(reader, "start_time"),
            GetDateTime(reader, "end_time"),
            GetDouble(reader, "lat"),
            GetDouble(reader, "lon"),
            GetString(reader, "address"),
            Math.Max(0, GetDouble(reader, "distance_km")),
            Math.Max(0, GetDouble(reader, "gap_hours")));
    }

    private static DateTime GetDateTime(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return DateTime.MinValue;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => dateTime,
            DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
            _ => DateTime.TryParse(Convert.ToString(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : DateTime.MinValue,
        };
    }

    private static string SqlDouble(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string HaversineSql(string lat1, string lon1, string lat2, string lon2)
    {
        return $$"""
            (6371.0 * 2.0 * asin(sqrt(
                pow(sin(radians(({{lat2}}) - ({{lat1}})) / 2.0), 2)
                + cos(radians({{lat1}})) * cos(radians({{lat2}}))
                * pow(sin(radians(({{lon2}}) - ({{lon1}})) / 2.0), 2)
            )))
            """;
    }

    private static double UsableKwh(ChargingScenario scenario)
    {
        return scenario.CapacityKwh * Math.Max(0, scenario.TargetSocPct - scenario.MinSocPct) / 100.0;
    }

    private static string Recommend(double totalMwh, double shortageMwh, bool roadSelection = false)
    {
        if (totalMwh <= 0)
        {
            return "Geen laadvraag in deze selectie.";
        }

        if (roadSelection)
        {
            return "Publieke corridor-lading analyseren voor deze rittenstroom.";
        }

        var share = shortageMwh / Math.Max(totalMwh, 0.001);
        if (shortageMwh <= 0.05 || share < 0.02)
        {
            return "Lokaal depotladen voldoet in dit scenario.";
        }

        if (share < 0.15)
        {
            return "Lokaal tekort beperkt; verhoog vermogen of voeg enkele publieke laadmomenten toe.";
        }

        return "Publieke corridor-lading nodig naast depotladen.";
    }

    private sealed record DemandEvent(
        string Wagencode,
        string Kentekens,
        DateOnly TripDate,
        string SelectionId,
        DateTime StartTime,
        DateTime EndTime,
        double Lat,
        double Lon,
        string Address,
        double DistanceKm,
        double GapHours);
}
