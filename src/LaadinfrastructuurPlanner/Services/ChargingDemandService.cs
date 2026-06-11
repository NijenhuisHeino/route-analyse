using System.Data.Common;
using System.Globalization;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    private const int DistanceBucketKm = 50;
    private const int FixedDistanceDistributionMaxKm = 750;

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
                        vehicle_key,
                        gap_hours,
                        day_km,
                        {{SqlDouble(scenario.CapacityKwh)}} AS demand_kwh
                    FROM filtered
                ),
                grouped AS (
                    SELECT
                        depot_id,
                        AVG(start_lat) AS lat,
                        AVG(start_lon) AS lon,
                        COALESCE(MODE(start_address), '') AS address,
                        COUNT(*) AS events,
                        COUNT(DISTINCT vehicle_key) AS unique_vehicles,
                        COALESCE(quantile_cont(gap_hours, 0.5), 0) AS median_gap_hours,
                        COALESCE(quantile_cont(day_km, 0.95), 0) AS p95_day_km,
                        COALESCE(SUM(demand_kwh), 0) / 1000.0 AS total_mwh,
                        COALESCE(SUM(demand_kwh), 0) / 1000.0 AS public_demand_mwh
                    FROM load
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
                    public_demand_mwh,
                    {{nearestSql}} AS nearest_charger_km
                FROM grouped g
                WHERE unique_vehicles >= {{minVehicles}}
                ORDER BY public_demand_mwh DESC, unique_vehicles DESC
                LIMIT {{limit}};
                """,
                r =>
                {
                    var totalMwh = GetDouble(r, "total_mwh");
                    var publicDemandMwh = GetDouble(r, "public_demand_mwh");
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
                        Math.Round(publicDemandMwh, 1),
                        Math.Round(GetDouble(r, "nearest_charger_km"), 1),
                        Recommend(totalMwh, publicDemandMwh));
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
                    CAST(prev_end_time AS TIMESTAMP) AS start_time,
                    CAST(day_start AS TIMESTAMP) AS end_time,
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

    public async Task<SelectionDetailResponse> GetStopLocationDetailAsync(
        StopLocationDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("overnight_events"))
        {
            return EmptySelection("cache_missing", "stop", null, "Vertrekheatmap is nog niet beschikbaar.", "Vertrekheatmap is nog niet beschikbaar.");
        }

        var normalized = NormalizeStopLocationDetailRequest(request);
        var scenario = NormalizeScenario(normalized.Scenario);
        var key = CacheKey("stop-location-detail", new { normalized, scenario });
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildStopLocationWhere(normalized);

            var rows = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    CAST(wagencode AS VARCHAR) AS wagencode,
                    COALESCE(CAST(kentekens AS VARCHAR), CAST(kenteken AS VARCHAR), '') AS kentekens,
                    CAST(trip_date AS DATE) AS trip_date,
                    CAST(trip_date AS VARCHAR) AS selection_id,
                    CAST(prev_end_time AS TIMESTAMP) AS start_time,
                    CAST(day_start AS TIMESTAMP) AS end_time,
                    CAST(start_lat AS DOUBLE) AS lat,
                    CAST(start_lon AS DOUBLE) AS lon,
                    COALESCE(CAST(start_address AS VARCHAR), '') AS address,
                    COALESCE(CAST(day_km AS DOUBLE), 0) AS distance_km,
                    COALESCE(CAST(gap_hours AS DOUBLE), 0) AS gap_hours
                FROM overnight_events
                WHERE {{where}}
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
                    WHERE {{where}}
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

            var title = string.IsNullOrWhiteSpace(normalized.Label)
                ? string.Create(CultureInfo.InvariantCulture, $"Vertrekritten vanaf {normalized.Lat:0.000}, {normalized.Lon:0.000}")
                : $"Vertrekritten vanaf {normalized.Label}";
            return BuildSelectionDetail(
                "stop",
                $"{normalized.Lat:0.000}:{normalized.Lon:0.000}",
                title,
                normalized.Lat,
                normalized.Lon,
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

            var dailyDistances = await QueryListAsync(
                connection,
                $$"""
                WITH selected_days AS (
                    SELECT DISTINCT wagencode, trip_date
                    FROM road_selection_index
                    WHERE {{where}}
                )
                SELECT COALESCE(CAST(v.day_km AS DOUBLE), 0) AS day_km
                FROM vehicle_days v
                JOIN selected_days d
                    ON CAST(v.wagencode AS VARCHAR) = d.wagencode
                    AND CAST(v.trip_date AS DATE) = d.trip_date
                ORDER BY day_km DESC
                LIMIT 50000;
                """,
                r => GetDouble(r, "day_km"),
                cancellationToken);

            var centerLat = (normalized.Road.Lat1 + normalized.Road.Lat2) / 2.0;
            var centerLon = (normalized.Road.Lon1 + normalized.Road.Lon2) / 2.0;
            var direction = CompassDirection(BearingDegrees(normalized.Road.Lat1, normalized.Road.Lon1, normalized.Road.Lat2, normalized.Road.Lon2));
            return BuildSelectionDetail(
                "road",
                null,
                string.Create(CultureInfo.InvariantCulture, $"Wegvlak {direction} · {centerLat:0.000}, {centerLon:0.000}"),
                centerLat,
                centerLon,
                rows,
                heat,
                scenario,
                isRoadSelection: true,
                dailyDistanceDistribution: BuildFixedDistanceDistribution(dailyDistances));
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
        bool isRoadSelection,
        DistanceDistribution? dailyDistanceDistribution = null)
    {
        var distribution = BuildDistanceDistribution(rows.Select(x => x.DistanceKm));
        dailyDistanceDistribution ??= distribution;
        var charging = BuildChargingProfile(rows, scenario, isRoadSelection);
        var vehicles = rows
            .GroupBy(VehicleGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var distances = group.Select(x => x.DistanceKm).Order().ToArray();
                var kentekens = CompactLicensePlates(group.SelectMany(x => SplitLicensePlates(x.Kentekens)));
                var wagencodes = CompactValues(group.Select(x => x.Wagencode));
                var demandPerDay = group.Select(x => Math.Max(0, x.DistanceKm * scenario.KwhPerKm)).ToArray();
                var requiredKw = group
                    .Select(x => RequiredKwForEvent(x, scenario, isRoadSelection))
                    .Order()
                    .ToArray();
                return new SelectionVehicleRow(
                    wagencodes,
                    kentekens,
                    group.LongCount(),
                    group.LongCount(),
                    Math.Round(group.Sum(x => x.DistanceKm), 1),
                    Math.Round(group.Average(x => x.DistanceKm), 1),
                    Math.Round(QuantileSorted(distances, 0.95), 1),
                    Math.Round(demandPerDay.Length == 0 ? 0 : demandPerDay.Average(), 1),
                    Math.Round(group.Sum(x => x.DistanceKm * scenario.KwhPerKm) / 1000.0, 2),
                    Math.Round(group.Average(x => x.GapHours), 1),
                    Math.Round(QuantileSorted(requiredKw, 0.95), 0));
            })
            .OrderByDescending(x => x.TotalMwh)
            .ThenByDescending(x => x.P95Km)
            .Take(100)
            .ToArray();

        var summary = new SelectionSummary(
            rows.LongCount(),
            rows.Select(VehicleGroupKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
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
            dailyDistanceDistribution,
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
        var eventLoads = rows
            .Select(row => BuildEventLoad(row, scenario, isRoadSelection))
            .ToArray();

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

        var hourlyProfile = BuildHourlyProfile(eventLoads);
        var weeklyProfile = BuildWeeklyProfile(eventLoads);
        var totalMwh = eventLoads.Sum(x => x.DemandKwh) / 1000.0;
        var shortageMwh = eventLoads.Sum(x => x.ShortageKwh) / 1000.0;
        var peakMw = weeklyProfile.Length == 0 ? 0 : weeklyProfile.Max(x => x.RequiredMw);
        var plugsAtPeak = scenario.KwPerPlug <= 0 ? 0 : (int)Math.Ceiling(peakMw * 1000.0 / scenario.KwPerPlug);

        return new ChargingProfile(
            rows.LongCount(),
            Math.Round(totalMwh, 1),
            Math.Round(shortageMwh, 1),
            Math.Round(peakMw, 2),
            plugsAtPeak,
            windows,
            hourlyProfile,
            weeklyProfile,
            Recommend(totalMwh, shortageMwh, isRoadSelection));
    }

    private static DemandEventLoad BuildEventLoad(DemandEvent row, ChargingScenario scenario, bool isRoadSelection)
    {
        var demandKwh = isRoadSelection
            ? Math.Max(0, row.DistanceKm * scenario.KwhPerKm)
            : Math.Max(0, scenario.CapacityKwh);
        var deliverableKwh = 0.0;
        var publicDemandKwh = demandKwh;
        var requiredKw = RequiredKwForEvent(row, scenario, isRoadSelection);
        var slot = row.StartTime == DateTime.MinValue
            ? "00:00"
            : row.StartTime.ToString("HH:00", CultureInfo.InvariantCulture);

        return new DemandEventLoad(row, slot, demandKwh, deliverableKwh, publicDemandKwh, requiredKw);
    }

    private static HourlyDemandCell[] BuildHourlyProfile(IReadOnlyList<DemandEventLoad> eventLoads)
    {
        var slots = new Dictionary<DateTime, HourAccumulator>();
        foreach (var load in eventLoads)
        {
            if (load.Row.StartTime == DateTime.MinValue || load.Row.EndTime == DateTime.MinValue || load.Row.EndTime <= load.Row.StartTime)
            {
                continue;
            }

            var cursor = new DateTime(load.Row.StartTime.Year, load.Row.StartTime.Month, load.Row.StartTime.Day, load.Row.StartTime.Hour, 0, 0);
            while (cursor < load.Row.EndTime)
            {
                var next = cursor.AddHours(1);
                var overlapStart = load.Row.StartTime > cursor ? load.Row.StartTime : cursor;
                var overlapEnd = load.Row.EndTime < next ? load.Row.EndTime : next;
                var overlapHours = Math.Max(0, (overlapEnd - overlapStart).TotalHours);
                if (overlapHours > 0)
                {
                    if (!slots.TryGetValue(cursor, out var accumulator))
                    {
                        accumulator = new HourAccumulator();
                        slots[cursor] = accumulator;
                    }

                    accumulator.VehicleKeys.Add(VehicleGroupKey(load.Row));
                    accumulator.Events++;
                    accumulator.DemandKwh += load.RequiredKw * overlapHours;
                    accumulator.RequiredKw += load.RequiredKw;
                }

                cursor = next;
            }
        }

        var peakPerHour = slots
            .Select(slot => new
            {
                Hour = slot.Key.Hour,
                Vehicles = slot.Value.VehicleKeys.LongCount(),
                slot.Value.Events,
                slot.Value.DemandKwh,
                slot.Value.RequiredKw,
            })
            .GroupBy(x => x.Hour)
            .Select(group => group
                .OrderByDescending(x => x.Vehicles)
                .ThenByDescending(x => x.RequiredKw)
                .First())
            .ToDictionary(x => x.Hour);

        return Enumerable.Range(0, 24)
            .Select(hour =>
            {
                peakPerHour.TryGetValue(hour, out var peak);
                var vehicles = peak?.Vehicles ?? 0;
                var events = peak?.Events ?? 0;
                var demandKwh = peak?.DemandKwh ?? 0;
                var requiredKw = peak?.RequiredKw ?? 0;
                return new HourlyDemandCell(
                    hour,
                    $"{hour:00}:00",
                    vehicles,
                    events,
                    Math.Round(demandKwh, 0),
                    Math.Round(requiredKw, 0),
                    Math.Round(requiredKw / 1000.0, 2));
            })
            .ToArray();
    }

    private static WeeklyDemandCell[] BuildWeeklyProfile(IReadOnlyList<DemandEventLoad> eventLoads)
    {
        var slots = new Dictionary<DateTime, HourAccumulator>();
        foreach (var load in eventLoads)
        {
            if (load.Row.StartTime == DateTime.MinValue || load.Row.EndTime == DateTime.MinValue || load.Row.EndTime <= load.Row.StartTime)
            {
                continue;
            }

            var cursor = new DateTime(load.Row.StartTime.Year, load.Row.StartTime.Month, load.Row.StartTime.Day, load.Row.StartTime.Hour, 0, 0);
            while (cursor < load.Row.EndTime)
            {
                var next = cursor.AddHours(1);
                var overlapStart = load.Row.StartTime > cursor ? load.Row.StartTime : cursor;
                var overlapEnd = load.Row.EndTime < next ? load.Row.EndTime : next;
                var overlapHours = Math.Max(0, (overlapEnd - overlapStart).TotalHours);
                if (overlapHours > 0)
                {
                    if (!slots.TryGetValue(cursor, out var accumulator))
                    {
                        accumulator = new HourAccumulator();
                        slots[cursor] = accumulator;
                    }

                    accumulator.VehicleKeys.Add(VehicleGroupKey(load.Row));
                    accumulator.Events++;
                    accumulator.DemandKwh += load.RequiredKw * overlapHours;
                    accumulator.RequiredKw += load.RequiredKw;
                    foreach (var plate in SplitLicensePlates(load.Row.Kentekens))
                    {
                        accumulator.Kentekens.Add(plate);
                    }

                    if (!string.IsNullOrWhiteSpace(load.Row.Wagencode))
                    {
                        accumulator.Wagencodes.Add(load.Row.Wagencode);
                    }

                    accumulator.AddVehicleDemand(load);
                }

                cursor = next;
            }
        }

        var peakPerWeekHour = slots
            .Select(slot => new
            {
                DayIndex = WeekDayIndex(slot.Key.DayOfWeek),
                Hour = slot.Key.Hour,
                Vehicles = slot.Value.VehicleKeys.LongCount(),
                slot.Value.Events,
                slot.Value.DemandKwh,
                slot.Value.RequiredKw,
                Kentekens = slot.Value.Kentekens.Order(StringComparer.OrdinalIgnoreCase).Take(40).ToArray(),
                Wagencodes = slot.Value.Wagencodes.Order(StringComparer.OrdinalIgnoreCase).Take(40).ToArray(),
                VehicleDemands = slot.Value.VehicleDemands
                    .Values
                    .OrderByDescending(x => x.RequiredKw)
                    .ThenBy(x => x.Wagencode, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Kenteken, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.ToRow())
                    .ToArray(),
            })
            .GroupBy(x => (x.DayIndex, x.Hour))
            .Select(group => group
                .OrderByDescending(x => x.RequiredKw)
                .ThenByDescending(x => x.Vehicles)
                .First())
            .ToDictionary(x => (x.DayIndex, x.Hour));

        return Enumerable.Range(0, 7)
            .SelectMany(day => Enumerable.Range(0, 24).Select(hour =>
            {
                peakPerWeekHour.TryGetValue((day, hour), out var peak);
                var vehicles = peak?.Vehicles ?? 0;
                var events = peak?.Events ?? 0;
                var demandKwh = peak?.DemandKwh ?? 0;
                var requiredKw = peak?.RequiredKw ?? 0;
                return new WeeklyDemandCell(
                    day,
                    WeekDayLabel(day),
                    hour,
                    $"{WeekDayLabel(day)} {hour:00}:00",
                    vehicles,
                    events,
                    Math.Round(demandKwh, 0),
                    Math.Round(requiredKw, 0),
                    Math.Round(requiredKw / 1000.0, 2),
                    peak?.Kentekens ?? [],
                    peak?.Wagencodes ?? [],
                    peak?.VehicleDemands ?? []);
            }))
            .ToArray();
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

    private static DistanceDistribution BuildFixedDistanceDistribution(IEnumerable<double> distances)
    {
        var values = distances.Where(x => x >= 0).Order().ToArray();
        var buckets = Enumerable.Range(0, FixedDistanceDistributionMaxKm / DistanceBucketKm)
            .Select(i =>
            {
                var from = i * DistanceBucketKm;
                var to = from + DistanceBucketKm;
                var count = values.LongCount(x => x >= from && (to == FixedDistanceDistributionMaxKm ? x >= from : x < to));
                return new DistanceBucket(from, to, count);
            })
            .ToArray();

        if (values.Length == 0)
        {
            return new DistanceDistribution(0, 0, 0, 0, 0, 0, 0, buckets);
        }

        var avg = values.Average();
        var variance = values.Sum(x => Math.Pow(x - avg, 2)) / values.Length;
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

    private static string CompactValues(IEnumerable<string> values)
    {
        var clean = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (clean.Length == 0)
        {
            return "-";
        }

        const int maxVisible = 3;
        var visible = clean.Take(maxVisible).ToArray();
        return clean.Length <= maxVisible
            ? string.Join(", ", visible)
            : string.Join(", ", visible) + $" +{clean.Length - maxVisible}";
    }

    private static string VehicleGroupKey(DemandEvent row)
    {
        var license = SplitLicensePlates(row.Kentekens).FirstOrDefault();
        return !string.IsNullOrWhiteSpace(license)
            ? license
            : row.Wagencode;
    }

    private static double RequiredKwForEvent(DemandEvent row, ChargingScenario scenario, bool isRoadSelection)
    {
        var demandKwh = isRoadSelection
            ? Math.Max(0, row.DistanceKm * scenario.KwhPerKm)
            : Math.Max(0, scenario.CapacityKwh);
        if (isRoadSelection)
        {
            return demandKwh;
        }

        return row.GapHours > 0 ? demandKwh / row.GapHours : demandKwh;
    }

    private static int WeekDayIndex(DayOfWeek dayOfWeek)
    {
        return dayOfWeek == DayOfWeek.Sunday ? 6 : (int)dayOfWeek - 1;
    }

    private static string WeekDayLabel(int dayIndex)
    {
        return dayIndex switch
        {
            0 => "Maandag",
            1 => "Dinsdag",
            2 => "Woensdag",
            3 => "Donderdag",
            4 => "Vrijdag",
            5 => "Zaterdag",
            _ => "Zondag"
        };
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
            new DistanceDistribution(0, 0, 0, 0, 0, 0, 0, []),
            [],
            [],
            new ChargingProfile(0, 0, 0, 0, 0, [], [], [], message),
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
            ZeZoneMode = request.ZeZoneMode,
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
            ZeZoneMode = normalized.ZeZoneMode,
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
            ZeZoneMode = normalized.ZeZoneMode,
            DepotId = request.DepotId.Trim(),
            Scenario = NormalizeScenario(request.Scenario),
        };
    }

    private StopLocationDetailRequest NormalizeStopLocationDetailRequest(StopLocationDetailRequest request)
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
            ZeZoneMode = normalized.ZeZoneMode,
            Lat = Math.Clamp(request.Lat, -90, 90),
            Lon = Math.Clamp(request.Lon, -180, 180),
            Label = request.Label?.Trim(),
            RadiusKm = Math.Clamp(request.RadiusKm, 0.1, 5),
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
            ZeZoneMode = normalized.ZeZoneMode,
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
        AddZeZoneTripExistsFilter(parts, filter.ZeZoneMode, "daily_trips", includeTripId: true);
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
        AddZeZoneTripExistsFilter(parts, filter.ZeZoneMode, "overnight_events", includeTripId: false);
        return string.Join(" AND ", parts);
    }

    private static void AddZeZoneTripExistsFilter(List<string> parts, string mode, string relation, bool includeTripId)
    {
        var tripMatch = includeTripId
            ? $"AND CAST(s.trip_id AS VARCHAR) = CAST({relation}.trip_id AS VARCHAR)"
            : "";

        if (string.Equals(mode, "in", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($$"""
                EXISTS (
                    SELECT 1
                    FROM stops s
                    WHERE CAST(s.wagencode AS VARCHAR) = CAST({{relation}}.wagencode AS VARCHAR)
                        AND CAST(s.trip_date AS DATE) = CAST({{relation}}.trip_date AS DATE)
                        {{tripMatch}}
                        AND COALESCE(CAST(s.in_zez AS BOOLEAN), false)
                )
                """);
        }
        else if (string.Equals(mode, "out", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($$"""
                EXISTS (
                    SELECT 1
                    FROM stops s
                    WHERE CAST(s.wagencode AS VARCHAR) = CAST({{relation}}.wagencode AS VARCHAR)
                        AND CAST(s.trip_date AS DATE) = CAST({{relation}}.trip_date AS DATE)
                        {{tripMatch}}
                        AND NOT COALESCE(CAST(s.in_zez AS BOOLEAN), false)
                )
                """);
        }
    }

    private static string BuildStopLocationWhere(StopLocationDetailRequest request)
    {
        var parts = new List<string>
        {
            "day_km >= 0",
            "start_lat IS NOT NULL",
            "start_lon IS NOT NULL",
            $"{HaversineSql("start_lat", "start_lon", SqlDouble(request.Lat), SqlDouble(request.Lon))} <= {SqlDouble(request.RadiusKm)}",
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

    private static string Recommend(double totalMwh, double publicDemandMwh, bool roadSelection = false)
    {
        if (totalMwh <= 0)
        {
            return "Geen laadvraag in deze selectie.";
        }

        if (roadSelection)
        {
            return "Wegvlakselectie met berekende corridor-laadvraag.";
        }

        return $"Publieke laadvraag: {publicDemandMwh:N1} MWh in dit scenario.";
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

    private sealed record DemandEventLoad(
        DemandEvent Row,
        string Slot,
        double DemandKwh,
        double DeliverableKwh,
        double ShortageKwh,
        double RequiredKw);

    private sealed class HourAccumulator
    {
        public HashSet<string> VehicleKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Kentekens { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Wagencodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VehicleDemandAccumulator> VehicleDemands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Events { get; set; }
        public double DemandKwh { get; set; }
        public double RequiredKw { get; set; }

        public void AddVehicleDemand(DemandEventLoad load)
        {
            var key = VehicleGroupKey(load.Row);
            if (string.IsNullOrWhiteSpace(key))
            {
                key = $"{load.Row.Wagencode}|{load.Row.Kentekens}|{load.Row.StartTime:O}";
            }

            if (!VehicleDemands.TryGetValue(key, out var vehicle))
            {
                vehicle = new VehicleDemandAccumulator(load.Row.Wagencode, SplitLicensePlates(load.Row.Kentekens).FirstOrDefault() ?? "-");
                VehicleDemands[key] = vehicle;
            }

            vehicle.DemandKwh += load.DemandKwh;
            vehicle.RequiredKw += load.RequiredKw;
            vehicle.StandingHours = Math.Max(vehicle.StandingHours, load.Row.GapHours);
            if (vehicle.StartTime == DateTime.MinValue || load.Row.StartTime < vehicle.StartTime)
            {
                vehicle.StartTime = load.Row.StartTime;
            }

            if (load.Row.EndTime > vehicle.EndTime)
            {
                vehicle.EndTime = load.Row.EndTime;
            }
        }
    }

    private sealed class VehicleDemandAccumulator(string wagencode, string kenteken)
    {
        public string Wagencode { get; } = wagencode;
        public string Kenteken { get; } = kenteken;
        public double DemandKwh { get; set; }
        public double RequiredKw { get; set; }
        public double StandingHours { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public WeeklyDemandVehicle ToRow()
        {
            var window = StartTime == DateTime.MinValue || EndTime == DateTime.MinValue
                ? ""
                : $"{StartTime:HH:mm}-{EndTime:HH:mm}";
            return new WeeklyDemandVehicle(
                Wagencode,
                Kenteken,
                Math.Round(DemandKwh, 0),
                Math.Round(RequiredKw, 0),
                Math.Round(StandingHours, 1),
                window);
        }
    }
}
