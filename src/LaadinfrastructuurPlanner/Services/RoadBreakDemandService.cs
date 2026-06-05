using System.Data.Common;
using System.Globalization;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    private const string RoadBreakRouteQuality = "laag: lineaire ritprogressie";

    public async Task<RoadBreakDemandMapResponse> GetRoadBreakDemandMapAsync(
        RoadBreakDemandRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("daily_trips"))
        {
            return EmptyRoadBreakDemandMap("cache_missing", "Ritdagregels zijn nog niet beschikbaar.", NormalizeRoadBreakDemandRequest(request), false);
        }

        var normalized = NormalizeRoadBreakDemandRequest(request);
        var key = CacheKey("road-break-demand-map", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            var result = await GetRoadBreakDemandCalculationAsync(ToRoadBreakEventRequest(normalized), cancellationToken);
            var lines = BuildRoadBreakDemandLines(result.Events, normalized);

            return new RoadBreakDemandMapResponse(
                "ok",
                null,
                normalized.WindowStartHours,
                normalized.WindowEndHours,
                normalized.BreakDurationHours,
                lines,
                result.Diagnostics,
                true);
        });
    }

    public async Task<RoadBreakDemandDetailResponse> GetRoadBreakDemandDetailAsync(
        RoadBreakDemandDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("daily_trips"))
        {
            return EmptyRoadBreakDemandDetail("cache_missing", "Ritdagregels zijn nog niet beschikbaar.", NormalizeRoadBreakDemandDetailRequest(request));
        }

        var normalized = NormalizeRoadBreakDemandDetailRequest(request);
        var key = CacheKey("road-break-demand-detail", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            var result = await GetRoadBreakDemandCalculationAsync(ToRoadBreakEventRequest(normalized), cancellationToken);
            var selected = result.Events
                .Where(x => IsRoadBreakEventInSelection(x, normalized.Road))
                .OrderBy(x => x.BreakStart)
                .ThenBy(x => x.Wagencode, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var profile = BuildRoadBreakQuarterProfile(selected, normalized.BreakDurationHours);
            var totalKwh = selected.Sum(x => x.DemandKwh);
            var peakMw = profile.Length == 0 ? 0 : profile.Max(x => x.RequiredMw);
            var vehicles = selected
                .Select(x => VehicleDemandKey(x.Wagencode, x.Kenteken))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .LongCount();
            var centerLat = (normalized.Road.Lat1 + normalized.Road.Lat2) / 2.0;
            var centerLon = (normalized.Road.Lon1 + normalized.Road.Lon2) / 2.0;
            var diagnostics = result.Diagnostics with
            {
                IncludedPassages = selected.Length,
                LowQualityMatches = selected.LongCount(x => x.RouteQuality.StartsWith("laag", StringComparison.OrdinalIgnoreCase)),
                ExcludedTrips = result.Diagnostics.ExcludedTrips,
                ExclusionReasons = BuildRoadBreakDetailReasons(result.Diagnostics, selected.Length)
            };

            return new RoadBreakDemandDetailResponse(
                "ok",
                null,
                $"Pauzelaadvraag wegvlak · {centerLat:0.000}, {centerLon:0.000}",
                Math.Round(peakMw, 3),
                Math.Round(totalKwh, 1),
                vehicles,
                selected.LongLength,
                profile,
                selected.Select(ToRoadBreakVehicleRow).ToArray(),
                diagnostics);
        });
    }

    private async Task<RoadBreakDemandCalculation> GetRoadBreakDemandCalculationAsync(
        RoadBreakDemandRequest request,
        CancellationToken cancellationToken)
    {
        var key = CacheKey("road-break-demand-events", request);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var trips = await QueryRoadBreakTripsAsync(connection, request, cancellationToken);
            return BuildRoadBreakDemand(trips, request);
        });
    }

    private async Task<List<RoadBreakTrip>> QueryRoadBreakTripsAsync(
        DuckDB.NET.Data.DuckDBConnection connection,
        RoadBreakDemandRequest request,
        CancellationToken cancellationToken)
    {
        var where = BuildDailyTripWhere(request);
        return await QueryListAsync(
            connection,
            $$"""
            SELECT
                CAST(wagencode AS VARCHAR) AS wagencode,
                COALESCE(CAST(kenteken AS VARCHAR), '') AS kenteken,
                COALESCE(CAST(kentekens AS VARCHAR), '') AS kentekens,
                CAST(trip_date AS DATE) AS trip_date,
                CAST(trip_id AS VARCHAR) AS trip_id,
                CAST(trip_start AS TIMESTAMP) AS trip_start,
                CAST(trip_end AS TIMESTAMP) AS trip_end,
                CAST(start_lat AS DOUBLE) AS start_lat,
                CAST(start_lon AS DOUBLE) AS start_lon,
                CAST(end_lat AS DOUBLE) AS end_lat,
                CAST(end_lon AS DOUBLE) AS end_lon,
                COALESCE(CAST(distance_km AS DOUBLE), 0) AS distance_km
            FROM daily_trips
            WHERE {{where}}
                AND start_lat IS NOT NULL
                AND start_lon IS NOT NULL
                AND end_lat IS NOT NULL
                AND end_lon IS NOT NULL
            ORDER BY wagencode, trip_date, trip_start, trip_id;
            """,
            ReadRoadBreakTrip,
            cancellationToken);
    }

    private static RoadBreakDemandCalculation BuildRoadBreakDemand(
        IReadOnlyList<RoadBreakTrip> trips,
        RoadBreakDemandRequest request)
    {
        if (trips.Count == 0)
        {
            return new RoadBreakDemandCalculation(
                [],
                new RoadBreakDemandDiagnostics(0, 0, 0, 0, ["included: 0 break-window passages", "excluded: 0 trips"]));
        }

        var resetLocations = BuildRoadBreakResetLocations(trips);
        var events = new List<RoadBreakEvent>();
        var excludedBeforeWindow = 0L;
        var excludedAfterWindow = 0L;
        var excludedInvalid = 0L;
        var excludedImplausibleSpeed = 0L;
        var resetCount = 0L;
        var maxVehicleDemandKwh = request.CapacityKwh * request.TargetSocPct / 100.0;

        foreach (var vehicleTrips in trips.GroupBy(x => x.Wagencode, StringComparer.OrdinalIgnoreCase))
        {
            DateTime? shiftStart = null;
            DateTime? previousEnd = null;
            double? previousEndLat = null;
            double? previousEndLon = null;
            var driveHours = 0.0;
            var shiftKm = 0.0;

            foreach (var trip in vehicleTrips.OrderBy(x => x.TripStart).ThenBy(x => x.TripId, StringComparer.OrdinalIgnoreCase))
            {
                if (shiftStart is null)
                {
                    shiftStart = trip.TripStart;
                }
                else if (previousEnd is not null
                    && (trip.TripStart - previousEnd.Value).TotalHours >= request.ShiftResetGapHours
                    && previousEndLat is not null
                    && previousEndLon is not null
                    && IsNearAnyResetLocation(previousEndLat.Value, previousEndLon.Value, trip.StartLat, trip.StartLon, resetLocations, request.ResetLocationRadiusKm))
                {
                    shiftStart = trip.TripStart;
                    driveHours = 0;
                    shiftKm = 0;
                    resetCount++;
                }

                var tripDriveHours = Math.Max(0, (trip.TripEnd - trip.TripStart).TotalHours);
                if (tripDriveHours <= 0 || trip.DistanceKm <= 0)
                {
                    excludedInvalid++;
                    previousEnd = trip.TripEnd;
                    previousEndLat = trip.EndLat;
                    previousEndLon = trip.EndLon;
                    continue;
                }

                var averageSpeedKmh = trip.DistanceKm / tripDriveHours;
                if (averageSpeedKmh > request.MaxAverageSpeedKmh)
                {
                    excludedImplausibleSpeed++;
                    previousEnd = trip.TripEnd;
                    previousEndLat = trip.EndLat;
                    previousEndLon = trip.EndLon;
                    continue;
                }

                var driveBefore = driveHours;
                var driveAfter = driveBefore + tripDriveHours;
                if (driveAfter < request.WindowStartHours)
                {
                    excludedBeforeWindow++;
                }
                else if (driveBefore > request.WindowEndHours)
                {
                    excludedAfterWindow++;
                }
                else
                {
                    var targetDrive = Math.Clamp(Math.Max(request.WindowStartHours, driveBefore), request.WindowStartHours, request.WindowEndHours);
                    var progress = Math.Clamp((targetDrive - driveBefore) / tripDriveHours, 0, 1);
                    var kmSinceShiftStart = shiftKm + trip.DistanceKm * progress;
                    var demandKwh = Math.Min(kmSinceShiftStart * request.KwhPerKm, maxVehicleDemandKwh);
                    var requiredKw = request.BreakDurationHours <= 0 ? 0 : demandKwh / request.BreakDurationHours;
                    var lat = Interpolate(trip.StartLat, trip.EndLat, progress);
                    var lon = Interpolate(trip.StartLon, trip.EndLon, progress);
                    var bearing = BearingDegrees(trip.StartLat, trip.StartLon, trip.EndLat, trip.EndLon);

                    events.Add(new RoadBreakEvent(
                        trip.Wagencode,
                        string.IsNullOrWhiteSpace(trip.Kentekens) ? trip.Kenteken : trip.Kentekens,
                        shiftStart.Value,
                        trip.TripStart.AddHours(Math.Max(0, targetDrive - driveBefore)),
                        Math.Round(targetDrive, 3),
                        kmSinceShiftStart,
                        demandKwh,
                        requiredKw,
                        lat,
                        lon,
                        trip.StartLat,
                        trip.StartLon,
                        trip.EndLat,
                        trip.EndLon,
                        DirectionBucket(bearing),
                        CompassDirection(bearing),
                        RoadBreakRouteQuality));
                }

                driveHours = driveAfter;
                shiftKm += trip.DistanceKm;
                previousEnd = trip.TripEnd;
                previousEndLat = trip.EndLat;
                previousEndLon = trip.EndLon;
            }
        }

        var reasons = new[]
        {
            $"included: {events.Count} break-window passages",
            $"excluded: {excludedBeforeWindow} trips before window",
            $"excluded: {excludedAfterWindow} trips after window",
            $"excluded: {excludedInvalid} trips without usable duration or distance",
            $"excluded: {excludedImplausibleSpeed} trips above {request.MaxAverageSpeedKmh:0} km/h average speed",
            $"resets: {resetCount} shift resets at repeated locations"
        };

        return new RoadBreakDemandCalculation(
            events,
            new RoadBreakDemandDiagnostics(
                trips.Count,
                events.Count,
                excludedBeforeWindow + excludedAfterWindow + excludedInvalid + excludedImplausibleSpeed,
                events.LongCount(),
                reasons));
    }

    private static RoadBreakDemandLine[] BuildRoadBreakDemandLines(
        IReadOnlyList<RoadBreakEvent> events,
        RoadBreakDemandRequest request)
    {
        var ordered = events
            .GroupBy(RoadBreakSegmentKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var rows = group.ToArray();
                var first = rows[0];
                var slotPeakKw = ExpandRoadBreakQuarterLoads(rows, request.BreakDurationHours)
                    .GroupBy(x => x.SlotStart)
                    .Select(x => x.Sum(y => y.Event.RequiredKw))
                    .DefaultIfEmpty(0)
                    .Max();
                var vehicles = rows
                    .Select(x => VehicleDemandKey(x.Wagencode, x.Kenteken))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .LongCount();
                var centerLat = rows.Average(x => x.BreakLat);
                var centerLon = rows.Average(x => x.BreakLon);
                var endpoints = BuildRoadBreakLineCoordinates(centerLat, centerLon, first.BearingBucket);

                return new RoadBreakDemandLine(
                    Math.Round(endpoints[0].Lat, 6),
                    Math.Round(endpoints[0].Lon, 6),
                    Math.Round(endpoints[1].Lat, 6),
                    Math.Round(endpoints[1].Lon, 6),
                    group.Key,
                    first.Direction,
                    Math.Round(slotPeakKw / 1000.0, 3),
                    Math.Round(rows.Sum(x => x.DemandKwh), 1),
                    vehicles,
                    rows.LongLength,
                    RoadBreakRouteQuality,
                    Math.Round(Math.Clamp(request.ResetLocationRadiusKm, 0.2, 20), 2),
                    endpoints);
            })
            .Where(x => x.Passages >= request.RoadThreshold)
            .OrderByDescending(x => x.Passages)
            .ThenByDescending(x => x.PeakMw)
            .ThenByDescending(x => x.TotalKwh)
            .ThenBy(x => x.SegmentId, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0 || request.RoadTopPercent <= 0)
        {
            return [];
        }

        var take = (int)Math.Ceiling(ordered.Length * Math.Clamp(request.RoadTopPercent, 0, 100) / 100.0);
        take = Math.Clamp(take, 1, 4_000);

        return ordered
            .Take(take)
            .OrderByDescending(x => x.Passages)
            .ThenByDescending(x => x.PeakMw)
            .ThenByDescending(x => x.TotalKwh)
            .ToArray();
    }

    private static RoadBreakQuarterCell[] BuildRoadBreakQuarterProfile(
        IReadOnlyList<RoadBreakEvent> events,
        double breakDurationHours)
    {
        return ExpandRoadBreakQuarterLoads(events, breakDurationHours)
            .GroupBy(x => x.SlotStart)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var loads = group.ToArray();
                var requiredKw = loads.Sum(x => x.Event.RequiredKw);
                return new RoadBreakQuarterCell(
                    group.Key,
                    group.Key.ToString("HH:mm", CultureInfo.InvariantCulture),
                    loads.Select(x => VehicleDemandKey(x.Event.Wagencode, x.Event.Kenteken)).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    Math.Round(requiredKw, 1),
                    Math.Round(requiredKw / 1000.0, 3),
                    Math.Round(loads.Sum(x => x.Event.RequiredKw * x.OverlapHours), 1));
            })
            .ToArray();
    }

    private static RoadBreakVehicleRow ToRoadBreakVehicleRow(RoadBreakEvent row)
    {
        return new RoadBreakVehicleRow(
            row.Wagencode,
            row.Kenteken,
            row.ShiftStart,
            row.BreakStart,
            Math.Round(row.DriveHoursSinceShiftStart, 2),
            Math.Round(row.KmSinceShiftStart, 1),
            Math.Round(row.DemandKwh, 1),
            Math.Round(row.RequiredKw, 1),
            row.RouteQuality);
    }

    private RoadBreakDemandRequest NormalizeRoadBreakDemandRequest(RoadBreakDemandRequest request)
    {
        var normalized = NormalizeFilter(request);
        var start = Math.Clamp(request.WindowStartHours, 0.5, 12);
        var end = Math.Clamp(request.WindowEndHours, start + 0.25, 14);
        return request with
        {
            DateFrom = normalized.DateFrom,
            DateTo = normalized.DateTo,
            VervoerderTypes = normalized.VervoerderTypes,
            Vervoerders = normalized.Vervoerders,
            Wagencodes = normalized.Wagencodes,
            MinDwellMin = normalized.MinDwellMin,
            RoadThreshold = normalized.RoadThreshold,
            RoadTopPercent = Math.Clamp(request.RoadTopPercent, 0, 100),
            MarkerTopN = normalized.MarkerTopN,
            ZeZoneMode = normalized.ZeZoneMode,
            KwhPerKm = Math.Clamp(request.KwhPerKm, 0.1, 5),
            CapacityKwh = Math.Clamp(request.CapacityKwh, 100, 1_500),
            TargetSocPct = Math.Clamp(request.TargetSocPct, 20, 100),
            WindowStartHours = start,
            WindowEndHours = end,
            BreakDurationHours = Math.Clamp(request.BreakDurationHours, 0.25, 3),
            ShiftResetGapHours = Math.Clamp(request.ShiftResetGapHours, 0.5, 12),
            ResetLocationRadiusKm = Math.Clamp(request.ResetLocationRadiusKm, 0.1, 5),
            MaxAverageSpeedKmh = Math.Clamp(request.MaxAverageSpeedKmh, 50, 120)
        };
    }

    private RoadBreakDemandDetailRequest NormalizeRoadBreakDemandDetailRequest(RoadBreakDemandDetailRequest request)
    {
        var normalized = NormalizeRoadBreakDemandRequest(request);
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
            KwhPerKm = normalized.KwhPerKm,
            CapacityKwh = normalized.CapacityKwh,
            TargetSocPct = normalized.TargetSocPct,
            WindowStartHours = normalized.WindowStartHours,
            WindowEndHours = normalized.WindowEndHours,
            BreakDurationHours = normalized.BreakDurationHours,
            ShiftResetGapHours = normalized.ShiftResetGapHours,
            ResetLocationRadiusKm = normalized.ResetLocationRadiusKm,
            MaxAverageSpeedKmh = normalized.MaxAverageSpeedKmh,
            Road = road with
            {
                Lat1 = Math.Clamp(road.Lat1, -90, 90),
                Lon1 = Math.Clamp(road.Lon1, -180, 180),
                Lat2 = Math.Clamp(road.Lat2, -90, 90),
                Lon2 = Math.Clamp(road.Lon2, -180, 180),
                RadiusKm = Math.Clamp(road.RadiusKm, 0.2, 100)
            }
        };
    }

    private static RoadBreakDemandMapResponse EmptyRoadBreakDemandMap(
        string status,
        string message,
        RoadBreakDemandRequest request,
        bool fromCache)
    {
        return new RoadBreakDemandMapResponse(
            status,
            message,
            request.WindowStartHours,
            request.WindowEndHours,
            request.BreakDurationHours,
            [],
            new RoadBreakDemandDiagnostics(0, 0, 0, 0, ["included: 0 break-window passages"]),
            fromCache);
    }

    private static RoadBreakDemandDetailResponse EmptyRoadBreakDemandDetail(
        string status,
        string message,
        RoadBreakDemandDetailRequest request)
    {
        return new RoadBreakDemandDetailResponse(
            status,
            message,
            "",
            0,
            0,
            0,
            0,
            [],
            [],
            new RoadBreakDemandDiagnostics(0, 0, 0, 0, ["included: 0 break-window passages"]));
    }

    private static RoadBreakTrip ReadRoadBreakTrip(DbDataReader reader)
    {
        return new RoadBreakTrip(
            GetString(reader, "wagencode"),
            GetString(reader, "kenteken"),
            GetString(reader, "kentekens"),
            GetDateOnly(reader, "trip_date") ?? DateOnly.MinValue,
            GetString(reader, "trip_id"),
            GetDateTime(reader, "trip_start"),
            GetDateTime(reader, "trip_end"),
            GetDouble(reader, "start_lat"),
            GetDouble(reader, "start_lon"),
            GetDouble(reader, "end_lat"),
            GetDouble(reader, "end_lon"),
            Math.Max(0, GetDouble(reader, "distance_km")));
    }

    private static RoadBreakResetLocation[] BuildRoadBreakResetLocations(IReadOnlyList<RoadBreakTrip> trips)
    {
        var eventThreshold = trips.Count < 30 ? 3 : 20;
        return trips
            .SelectMany(trip => new[]
            {
                new RoadBreakLocationEvent(trip.Wagencode, Math.Round(trip.StartLat, 3), Math.Round(trip.StartLon, 3)),
                new RoadBreakLocationEvent(trip.Wagencode, Math.Round(trip.EndLat, 3), Math.Round(trip.EndLon, 3))
            })
            .GroupBy(x => CoordinateKey(x.Lat, x.Lon))
            .Where(group =>
                group.Select(x => x.Wagencode).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 5
                || group.Count() >= eventThreshold)
            .Select(group => new RoadBreakResetLocation(group.Average(x => x.Lat), group.Average(x => x.Lon)))
            .ToArray();
    }

    private static bool IsNearAnyResetLocation(
        double previousLat,
        double previousLon,
        double currentLat,
        double currentLon,
        IReadOnlyList<RoadBreakResetLocation> resetLocations,
        double radiusKm)
    {
        return resetLocations.Any(location =>
            HaversineKm(previousLat, previousLon, location.Lat, location.Lon) <= radiusKm
            && HaversineKm(currentLat, currentLon, location.Lat, location.Lon) <= radiusKm);
    }

    private static bool IsRoadBreakEventInSelection(RoadBreakEvent row, RoadSelection road)
    {
        return DistancePointToSegmentKm(row.BreakLat, row.BreakLon, road.Lat1, road.Lon1, road.Lat2, road.Lon2) <= road.RadiusKm;
    }

    private static double DistancePointToSegmentKm(
        double pointLat,
        double pointLon,
        double segmentLat1,
        double segmentLon1,
        double segmentLat2,
        double segmentLon2)
    {
        var meanLat = DegreesToRadians((pointLat + segmentLat1 + segmentLat2) / 3.0);
        const double kmPerDegreeLat = 111.32;
        var kmPerDegreeLon = Math.Max(1, kmPerDegreeLat * Math.Cos(meanLat));
        var px = pointLon * kmPerDegreeLon;
        var py = pointLat * kmPerDegreeLat;
        var ax = segmentLon1 * kmPerDegreeLon;
        var ay = segmentLat1 * kmPerDegreeLat;
        var bx = segmentLon2 * kmPerDegreeLon;
        var by = segmentLat2 * kmPerDegreeLat;
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0)
        {
            return HaversineKm(pointLat, pointLon, segmentLat1, segmentLon1);
        }

        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
        var closestX = ax + t * dx;
        var closestY = ay + t * dy;
        var x = px - closestX;
        var y = py - closestY;
        return Math.Sqrt(x * x + y * y);
    }

    private static RoadPoint[] BuildRoadBreakLineCoordinates(double lat, double lon, int bearingBucket)
    {
        var bearing = bearingBucket * 45.0;
        const double halfLengthKm = 0.75;
        return
        [
            ProjectPoint(lat, lon, (bearing + 180.0) % 360.0, halfLengthKm),
            ProjectPoint(lat, lon, bearing, halfLengthKm)
        ];
    }

    private static RoadPoint ProjectPoint(double lat, double lon, double bearingDegrees, double distanceKm)
    {
        const double earthRadiusKm = 6371.0;
        var angularDistance = distanceKm / earthRadiusKm;
        var bearing = DegreesToRadians(bearingDegrees);
        var lat1 = DegreesToRadians(lat);
        var lon1 = DegreesToRadians(lon);
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angularDistance)
            + Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(bearing));
        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(lat1),
            Math.Cos(angularDistance) - Math.Sin(lat1) * Math.Sin(lat2));

        return new RoadPoint(
            Math.Round(RadiansToDegrees(lat2), 6),
            Math.Round(RadiansToDegrees(lon2), 6));
    }

    private static DateTime QuarterSlot(DateTime value)
    {
        var minutes = value.Minute - value.Minute % 15;
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, minutes, 0, value.Kind);
    }

    private static string RoadBreakSegmentKey(RoadBreakEvent row)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"pauze:{row.BearingBucket}:{Math.Round(row.BreakLat, 3):0.000}:{Math.Round(row.BreakLon, 3):0.000}");
    }

    private static string VehicleDemandKey(string wagencode, string kenteken)
    {
        return string.IsNullOrWhiteSpace(kenteken) ? wagencode : kenteken;
    }

    private static string[] BuildRoadBreakDetailReasons(RoadBreakDemandDiagnostics diagnostics, int selectedCount)
    {
        return
        [
            $"included: {selectedCount} selected break-window passages",
            .. diagnostics.ExclusionReasons.Where(x => !x.StartsWith("included:", StringComparison.OrdinalIgnoreCase))
        ];
    }

    private static double Interpolate(double start, double end, double progress)
    {
        return start + (end - start) * progress;
    }

    private static RoadBreakDemandRequest ToRoadBreakEventRequest(RoadBreakDemandRequest request)
    {
        return new RoadBreakDemandRequest
        {
            DateFrom = request.DateFrom,
            DateTo = request.DateTo,
            VervoerderTypes = request.VervoerderTypes,
            Vervoerders = request.Vervoerders,
            Wagencodes = request.Wagencodes,
            MinDwellMin = request.MinDwellMin,
            RoadThreshold = 1,
            RoadTopPercent = 100,
            MarkerTopN = request.MarkerTopN,
            ZeZoneMode = request.ZeZoneMode,
            KwhPerKm = request.KwhPerKm,
            CapacityKwh = request.CapacityKwh,
            TargetSocPct = request.TargetSocPct,
            WindowStartHours = request.WindowStartHours,
            WindowEndHours = request.WindowEndHours,
            BreakDurationHours = request.BreakDurationHours,
            ShiftResetGapHours = request.ShiftResetGapHours,
            ResetLocationRadiusKm = request.ResetLocationRadiusKm,
            MaxAverageSpeedKmh = request.MaxAverageSpeedKmh
        };
    }

    private static RoadBreakQuarterLoad[] ExpandRoadBreakQuarterLoads(
        IReadOnlyList<RoadBreakEvent> events,
        double breakDurationHours)
    {
        var loads = new List<RoadBreakQuarterLoad>();
        foreach (var row in events)
        {
            var start = row.BreakStart;
            var end = row.BreakStart.AddHours(Math.Max(0.25, breakDurationHours));
            for (var slot = QuarterSlot(start); slot < end; slot = slot.AddMinutes(15))
            {
                var slotEnd = slot.AddMinutes(15);
                var overlapStart = start > slot ? start : slot;
                var overlapEnd = end < slotEnd ? end : slotEnd;
                var overlapHours = (overlapEnd - overlapStart).TotalHours;
                if (overlapHours > 0)
                {
                    loads.Add(new RoadBreakQuarterLoad(slot, row, overlapHours));
                }
            }
        }

        return loads.ToArray();
    }

    private sealed record RoadBreakTrip(
        string Wagencode,
        string Kenteken,
        string Kentekens,
        DateOnly TripDate,
        string TripId,
        DateTime TripStart,
        DateTime TripEnd,
        double StartLat,
        double StartLon,
        double EndLat,
        double EndLon,
        double DistanceKm);

    private sealed record RoadBreakEvent(
        string Wagencode,
        string Kenteken,
        DateTime ShiftStart,
        DateTime BreakStart,
        double DriveHoursSinceShiftStart,
        double KmSinceShiftStart,
        double DemandKwh,
        double RequiredKw,
        double BreakLat,
        double BreakLon,
        double TripStartLat,
        double TripStartLon,
        double TripEndLat,
        double TripEndLon,
        int BearingBucket,
        string Direction,
        string RouteQuality);

    private sealed record RoadBreakResetLocation(double Lat, double Lon);

    private sealed record RoadBreakLocationEvent(string Wagencode, double Lat, double Lon);

    private sealed record RoadBreakQuarterLoad(DateTime SlotStart, RoadBreakEvent Event, double OverlapHours);

    private sealed record RoadBreakDemandCalculation(
        List<RoadBreakEvent> Events,
        RoadBreakDemandDiagnostics Diagnostics);
}
