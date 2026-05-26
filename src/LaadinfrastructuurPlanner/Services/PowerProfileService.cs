using System.Data.Common;
using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    private static readonly string[] ChargeWindowActions = ["wait_task_available", "wait_after", "wait_action", "pause"];

    public async Task<PowerProfileResponse> GetPowerProfilesAsync(
        PowerProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("route_actions"))
        {
            return new PowerProfileResponse("missing", "Route-acties zijn nog niet beschikbaar.", [], [], [], false);
        }

        var normalized = NormalizePowerRequest(request);
        var key = CacheKey("power-profiles", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var events = MergeOverlappingPresenceWindows(ToPresenceWindows(await QueryPowerEventsAsync(connection, BuildPowerWhere(normalized, onlyChargeWindows: false), cancellationToken)));
            var profiles = BuildLocationProfiles(events, normalized, includeVehicleDemands: false)
                .OrderByDescending(x => x.PeakKw)
                .ThenByDescending(x => x.UniqueVehicles)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(normalized.TopLocations)
                .ToArray();
            var selectedIds = profiles.Select(x => x.LocationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var heatmap = profiles
                .SelectMany(profile => profile.HourlyProfile.Select(hour => new PowerHeatmapCell(
                    profile.LocationId,
                    string.IsNullOrWhiteSpace(profile.Name) ? profile.Address : profile.Name,
                    hour.Hour,
                    hour.Vehicles,
                    hour.RequiredKw,
                    hour.RequiredMw,
                    hour.AnomalyFlag)))
                .ToArray();
            var scenarios = BuildScenarioProfiles(events.Where(x => selectedIds.Contains(x.LocationId)).ToArray(), normalized);
            return new PowerProfileResponse("ok", null, profiles, heatmap, scenarios, true);
        });
    }

    public async Task<PowerLocationProfileResponse> GetPowerLocationProfileAsync(
        PowerLocationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("route_actions"))
        {
            return new PowerLocationProfileResponse("missing", "Route-acties zijn nog niet beschikbaar.", null, [], [], false);
        }

        var normalized = NormalizePowerLocationRequest(request);
        var key = CacheKey("power-location-profile", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var events = MergeOverlappingPresenceWindows(ToPresenceWindows(await QueryPowerEventsAsync(connection, BuildPowerWhere(normalized, onlyChargeWindows: false), cancellationToken)))
                .Where(x => string.Equals(x.LocationId, normalized.LocationId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var profile = BuildLocationProfiles(events, normalized, includeVehicleDemands: true).FirstOrDefault();
            var daily = BuildDailyMetrics(events, normalized);
            var scenarios = BuildScenarioProfiles(events, normalized);
            return new PowerLocationProfileResponse(
                "ok",
                profile is null ? "Geen laadvensters gevonden voor deze standplaats." : null,
                profile,
                daily,
                scenarios,
                true);
        });
    }

    public async Task<PowerDiagnosticsResponse> GetPowerDiagnosticsAsync(
        AnalysisFilter filter,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops || !_store.HasView("route_actions"))
        {
            return new PowerDiagnosticsResponse("missing", 0, 0, 0, 0, 0, 0, 0, [], PowerAssumptionTexts(), false);
        }

        var normalized = NormalizeFilter(filter);
        var key = CacheKey("power-diagnostics", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var powerFilter = new PowerProfileRequest
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
            };
            var where = BuildPowerWhere(powerFilter, onlyChargeWindows: false);
            var chargeWhere = BuildPowerWhere(powerFilter, onlyChargeWindows: true);

            var totals = await QuerySingleAsync(
                connection,
                $$"""
                SELECT
                    COUNT(*) AS total_actions,
                    SUM(CASE WHEN actie_soort IN ({{SqlStringList(ChargeWindowActions)}}) THEN 1 ELSE 0 END) AS charge_actions,
                    SUM(CASE WHEN location_id LIKE 'unknown_location:%' OR lat IS NULL OR lon IS NULL THEN 1 ELSE 0 END) AS missing_location_actions,
                    SUM(CASE WHEN vehicle_class = 'unknown' THEN 1 ELSE 0 END) AS unknown_vehicle_class_actions
                FROM route_actions
                WHERE {{where}};
                """,
                r => (
                    Total: GetInt64(r, "total_actions"),
                    Charge: GetInt64(r, "charge_actions"),
                    MissingLocation: GetInt64(r, "missing_location_actions"),
                    UnknownClass: GetInt64(r, "unknown_vehicle_class_actions")),
                cancellationToken);

            var routesWithoutWait = await ScalarLongAsync(
                connection,
                $$"""
                WITH selected_routes AS (
                    SELECT DISTINCT wagencode, trip_date, trip_id
                    FROM route_actions
                    WHERE {{where}}
                ),
                charge_routes AS (
                    SELECT DISTINCT wagencode, trip_date, trip_id
                    FROM route_actions
                    WHERE {{chargeWhere}}
                )
                SELECT COUNT(*)
                FROM selected_routes s
                LEFT JOIN charge_routes c USING (wagencode, trip_date, trip_id)
                WHERE c.trip_id IS NULL;
                """,
                cancellationToken);

            var classCounts = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    vehicle_class,
                    COUNT(DISTINCT COALESCE(NULLIF(kenteken_norm, ''), wagencode)) AS vehicles,
                    COUNT(*) AS events
                FROM route_actions
                WHERE {{where}}
                GROUP BY vehicle_class
                ORDER BY vehicle_class;
                """,
                r => new VehicleClassCount(GetString(r, "vehicle_class"), GetInt64(r, "vehicles"), GetInt64(r, "events")),
                cancellationToken);

            var fleetVehicles = 0L;
            var fleetMatched = 0L;
            if (!string.IsNullOrWhiteSpace(_store.Options.FleetExcelPath) && File.Exists(_store.Options.FleetExcelPath))
            {
                var fleetKeys = LoadFleetVehicleKeys(_store.Options.FleetExcelPath);
                fleetVehicles = fleetKeys.Count;
                if (fleetVehicles > 0)
                {
                    var values = string.Join(", ", fleetKeys.Select(DuckDbRouteStore.SqlString));
                    fleetMatched = await ScalarLongAsync(
                        connection,
                        $"SELECT COUNT(DISTINCT COALESCE(NULLIF(kenteken_norm, ''), wagencode)) FROM route_actions WHERE COALESCE(NULLIF(kenteken_norm, ''), wagencode) IN ({values});",
                        cancellationToken);
                }
            }

            return new PowerDiagnosticsResponse(
                "ok",
                totals.Total,
                totals.Charge,
                totals.MissingLocation,
                totals.UnknownClass,
                routesWithoutWait,
                fleetVehicles,
                fleetMatched,
                classCounts.ToArray(),
                PowerAssumptionTexts(),
                true);
        });
    }

    public async Task<PowerReportExportResponse> ExportNieuwegeinPowerReportAsync(CancellationToken cancellationToken = default)
    {
        var outputDir = Path.Combine(_store.Options.RepoRoot, "out", "nieuwegein");
        Directory.CreateDirectory(outputDir);

        var profiles = await GetPowerProfilesAsync(new PowerProfileRequest
        {
            VehicleClasses = ["own"],
            TopLocations = 50,
            ScenarioYears = [2027, 2030],
        }, cancellationToken);

        var selected = profiles.Locations.FirstOrDefault(IsFocusLocation)
            ?? profiles.Locations.OrderByDescending(x => x.PeakKw).FirstOrDefault();
        if (selected is null)
        {
            return new PowerReportExportResponse("missing", "Geen laadvensters gevonden voor export.", outputDir, []);
        }

        var detail = await GetPowerLocationProfileAsync(new PowerLocationProfileRequest
        {
            LocationId = selected.LocationId,
            VehicleClasses = ["own"],
            ScenarioYears = [2027, 2030],
        }, cancellationToken);

        var files = new List<string>();
        var meta = new ExportMetadata(
            GeneratedAtUtc: DateTime.UtcNow,
            SoftwareVersion: typeof(RouteAnalysisService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            FocusLocationId: selected.LocationId,
            FocusLocationName: selected.Name,
            FocusAddress: selected.Address,
            CapacityKwh: 590,
            MaxVehicleKw: 400,
            SiteLimitMw: 1.4,
            ScenarioYears: [2027, 2030],
            VehicleClasses: ["own"],
            EnergyAssumptions: _store.Options.VehicleEnergyAssumptions,
            FleetRolloutMode: _store.Options.FleetRolloutMode,
            ScenarioInflows: _store.Options.ScenarioInflows);

        var hourlyCsv = Path.Combine(outputDir, "nieuwegein_hourly_profile.csv");
        await File.WriteAllTextAsync(hourlyCsv, ToHourlyCsv(selected), cancellationToken);
        files.Add(hourlyCsv);
        await WriteMetaAsync(hourlyCsv, meta, cancellationToken);
        files.Add(hourlyCsv + ".meta.json");

        var dailyCsv = Path.Combine(outputDir, "nieuwegein_daily_metrics.csv");
        await File.WriteAllTextAsync(dailyCsv, ToDailyCsv(detail.DailyMetrics), cancellationToken);
        files.Add(dailyCsv);
        await WriteMetaAsync(dailyCsv, meta, cancellationToken);
        files.Add(dailyCsv + ".meta.json");

        var scenarioCsv = Path.Combine(outputDir, "nieuwegein_scenarios_2027_2030.csv");
        await File.WriteAllTextAsync(scenarioCsv, ToScenarioCsv(detail.Scenarios), cancellationToken);
        files.Add(scenarioCsv);
        await WriteMetaAsync(scenarioCsv, meta, cancellationToken);
        files.Add(scenarioCsv + ".meta.json");

        var html = Path.Combine(outputDir, "nieuwegein_power_report.html");
        await File.WriteAllTextAsync(html, BuildReportHtml(selected, detail), cancellationToken);
        files.Add(html);

        var combinedCsv = Path.Combine(outputDir, "top5_own_current_heatmap.csv");
        await File.WriteAllTextAsync(combinedCsv, ToHeatmapCsv(profiles.Heatmap), cancellationToken);
        files.Add(combinedCsv);
        await WriteMetaAsync(combinedCsv, meta, cancellationToken);
        files.Add(combinedCsv + ".meta.json");

        var parquet = Path.Combine(outputDir, "top5_own_current_heatmap.parquet");
        await WriteHeatmapParquetAsync(profiles.Heatmap, parquet, cancellationToken);
        files.Add(parquet);
        await WriteMetaAsync(parquet, meta, cancellationToken);
        files.Add(parquet + ".meta.json");

        var message = IsFocusLocation(selected)
            ? "Nieuwegein-export geschreven."
            : $"Focuslocatie niet gevonden; export gebruikt hoogste pieklocatie: {selected.Name}.";
        return new PowerReportExportResponse("ok", message, outputDir, files.ToArray());
    }

    private async Task<List<PowerEvent>> QueryPowerEventsAsync(
        DuckDBConnection connection,
        string where,
        CancellationToken cancellationToken)
    {
        return await QueryListAsync(
            connection,
            $$"""
            SELECT
                wagencode,
                COALESCE(NULLIF(kenteken_norm, ''), wagencode) AS vehicle_key,
                COALESCE(kenteken, '') AS kenteken,
                vehicle_class,
                trip_date,
                trip_id,
                action_seq,
                actie_soort,
                location_id,
                COALESCE(NULLIF(locatie_naam, ''), adres, location_id) AS location_name,
                COALESCE(adres, '') AS address,
                COALESCE(wagentype_omschrijving, '') AS vehicle_type,
                GREATEST(COALESCE(CAST(afstand_km_trip AS DOUBLE), 0), COALESCE(CAST(afstand_km AS DOUBLE), 0)) AS distance_km,
                gepland_start,
                gepland_eind,
                dwell_min,
                lat,
                lon
            FROM route_actions
            WHERE {{where}}
            ORDER BY wagencode, trip_date, trip_id, gepland_start, action_seq;
            """,
            ReadPowerEvent,
            cancellationToken);
    }

    private bool IsFocusLocation(PowerLocationProfile profile)
    {
        return profile.Address.Contains("Groteweerd", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Groteweerd", StringComparison.OrdinalIgnoreCase)
            || profile.Address.Contains("Nieuwegein", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Nieuwegein", StringComparison.OrdinalIgnoreCase)
            || _store.Options.FocusLocationAlias.Contains(profile.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static PowerEvent ReadPowerEvent(DbDataReader reader)
    {
        var vehicleType = GetString(reader, "vehicle_type");
        return new PowerEvent(
            GetString(reader, "wagencode"),
            GetString(reader, "vehicle_key"),
            GetString(reader, "kenteken"),
            GetString(reader, "vehicle_class"),
            GetDateOnly(reader, "trip_date") ?? DateOnly.MinValue,
            GetString(reader, "trip_id"),
            GetInt32(reader, "action_seq"),
            GetString(reader, "actie_soort"),
            GetString(reader, "location_id"),
            GetString(reader, "location_name"),
            GetString(reader, "address"),
            vehicleType,
            Math.Max(0, GetDouble(reader, "distance_km")),
            GetDateTime(reader, "gepland_start"),
            GetDateTime(reader, "gepland_eind"),
            Math.Max(0, GetDouble(reader, "dwell_min")),
            GetDouble(reader, "lat"),
            GetDouble(reader, "lon"));
    }

    private static PowerEvent[] ToPresenceWindows(IReadOnlyList<PowerEvent> actions)
    {
        var windows = new List<PowerEvent>();
        foreach (var route in actions
            .Where(x => x.StartTime != DateTime.MinValue && x.EndTime > x.StartTime)
            .GroupBy(x => (x.VehicleKey, x.TripDate, x.TripId)))
        {
            var ordered = route
                .OrderBy(x => x.StartTime)
                .ThenBy(x => x.ActionSeq)
                .ToArray();
            var start = 0;
            while (start < ordered.Length)
            {
                var end = start + 1;
                while (end < ordered.Length && string.Equals(ordered[end].LocationId, ordered[start].LocationId, StringComparison.OrdinalIgnoreCase))
                {
                    end++;
                }

                var segment = ordered[start..end];
                if (segment.Any(x => ChargeWindowActions.Contains(x.ActionType, StringComparer.OrdinalIgnoreCase)))
                {
                    var first = segment[0];
                    var presenceStart = segment
                        .Select(x => string.Equals(x.ActionType, "travel", StringComparison.OrdinalIgnoreCase) ? x.EndTime : x.StartTime)
                        .Min();
                    var presenceEnd = segment.Max(x => x.EndTime);
                    if (presenceEnd > presenceStart)
                    {
                        windows.Add(first with
                        {
                            StartTime = presenceStart,
                            EndTime = presenceEnd,
                            DwellMin = (presenceEnd - presenceStart).TotalMinutes,
                        });
                    }
                }

                start = end;
            }
        }

        return windows.ToArray();
    }

    private static PowerEvent[] MergeOverlappingPresenceWindows(IReadOnlyList<PowerEvent> windows)
    {
        var merged = new List<PowerEvent>();
        foreach (var group in windows
            .Where(x => x.StartTime != DateTime.MinValue && x.EndTime > x.StartTime)
            .GroupBy(x => $"{VehicleIdentity(x)}\u001f{x.LocationId}", StringComparer.OrdinalIgnoreCase))
        {
            PowerEvent? current = null;
            foreach (var window in group.OrderBy(x => x.StartTime).ThenBy(x => x.EndTime))
            {
                if (current is null)
                {
                    current = window;
                    continue;
                }

                if (window.StartTime <= current.EndTime.AddMinutes(5))
                {
                    var endTime = window.EndTime > current.EndTime ? window.EndTime : current.EndTime;
                    current = current with
                    {
                        EndTime = endTime,
                        DwellMin = (endTime - current.StartTime).TotalMinutes,
                    };
                    continue;
                }

                merged.Add(current);
                current = window;
            }

            if (current is not null)
            {
                merged.Add(current);
            }
        }

        return merged
            .OrderBy(x => x.Wagencode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.TripDate)
            .ThenBy(x => x.StartTime)
            .ToArray();
    }

    private static string VehicleIdentity(PowerEvent powerEvent)
    {
        return string.IsNullOrWhiteSpace(powerEvent.VehicleKey)
            ? $"{powerEvent.Wagencode}|{powerEvent.Kenteken}"
            : powerEvent.VehicleKey;
    }

    private PowerLocationProfile[] BuildLocationProfiles(IReadOnlyList<PowerEvent> events, PowerProfileRequest request, bool includeVehicleDemands)
    {
        return events
            .Where(x => x.StartTime != DateTime.MinValue && x.EndTime > x.StartTime)
            .GroupBy(x => x.LocationId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var hourly = BuildHourlyPowerProfile(group.ToArray(), request, includeVehicleDemands);
                var peak = hourly.Length == 0 ? 0 : hourly.Max(x => x.RequiredKw);
                return new PowerLocationProfile(
                    group.Key,
                    ModeText(group.Select(x => x.LocationName)),
                    ModeText(group.Select(x => x.Address)),
                    Math.Round(group.Where(x => x.Lat != 0).Select(x => x.Lat).DefaultIfEmpty(0).Average(), 6),
                    Math.Round(group.Where(x => x.Lon != 0).Select(x => x.Lon).DefaultIfEmpty(0).Average(), 6),
                    group.Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.Where(x => x.VehicleClass == "own").Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.Where(x => x.VehicleClass == "charter").Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.Select(x => x.TripId).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.LongCount(),
                    Math.Round(group.Average(x => x.DwellMin), 1),
                    Math.Round(peak, 0),
                    hourly);
            })
            .ToArray();
    }

    private PowerDailyMetric[] BuildDailyMetrics(IReadOnlyList<PowerEvent> events, PowerProfileRequest request)
    {
        return events
            .GroupBy(x => x.TripDate)
            .OrderBy(x => x.Key)
            .Select(group =>
            {
                var hourly = BuildHourlyPowerProfile(group.ToArray(), request, includeVehicleDemands: false);
                return new PowerDailyMetric(
                    group.Key,
                    group.Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.Where(x => x.VehicleClass == "own").Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.Where(x => x.VehicleClass == "charter").Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.Select(x => x.TripId).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    group.LongCount(),
                    Math.Round(group.Average(x => x.DwellMin), 1),
                    hourly.Length == 0 ? 0 : hourly.Max(x => x.RequiredKw),
                    hourly.Any(x => x.AnomalyFlag));
            })
            .ToArray();
    }

    private PowerHourlyCell[] BuildHourlyPowerProfile(IReadOnlyList<PowerEvent> events, PowerProfileRequest request, bool includeVehicleDemands = false)
    {
        var capacityKwh = request.CapacityKwh;
        var maxVehicleKw = request.MaxVehicleKw;
        var siteLimitMw = request.SiteLimitMw;
        var kwhPerKm = request.KwhPerKm > 0 ? request.KwhPerKm : 1.2;
        var minSoc = Math.Clamp(request.MinSocPct, 0, 50);
        var targetSoc = Math.Clamp(Math.Max(request.TargetSocPct, minSoc + 1), 20, 100);
        var usablePerVehicleKwh = capacityKwh * Math.Max(0, targetSoc - minSoc) / 100.0;
        var siteLimitKw = siteLimitMw > 0 ? siteLimitMw * 1000.0 : double.PositiveInfinity;
        var slots = new Dictionary<DateTime, PowerAccumulator>();
        foreach (var powerEvent in events)
        {
            if (powerEvent.EndTime <= powerEvent.StartTime || maxVehicleKw <= 0) continue;

            // Energy this vehicle actually needs at this stop: distance reverse-engineered,
            // capped at usable SoC window. If no distance recorded, fall back to usable.
            var distanceDemandKwh = Math.Max(0, powerEvent.DistanceKm * kwhPerKm);
            var demandKwh = distanceDemandKwh > 0
                ? Math.Min(distanceDemandKwh, usablePerVehicleKwh)
                : usablePerVehicleKwh;
            if (demandKwh <= 0) continue;

            // Front-loaded charging: pull max power until energy budget is depleted.
            var standingHours = (powerEvent.EndTime - powerEvent.StartTime).TotalHours;
            var timeToFullHours = Math.Min(standingHours, demandKwh / maxVehicleKw);
            var chargeEnd = powerEvent.StartTime.AddHours(timeToFullHours);

            var cursor = new DateTime(powerEvent.StartTime.Year, powerEvent.StartTime.Month, powerEvent.StartTime.Day, powerEvent.StartTime.Hour, 0, 0);
            while (cursor < powerEvent.EndTime)
            {
                var next = cursor.AddHours(1);
                var presenceStart = powerEvent.StartTime > cursor ? powerEvent.StartTime : cursor;
                var presenceEnd = powerEvent.EndTime < next ? powerEvent.EndTime : next;
                if (presenceEnd <= presenceStart)
                {
                    cursor = next;
                    continue;
                }

                var chargeStart = presenceStart > powerEvent.StartTime ? presenceStart : powerEvent.StartTime;
                var chargeStop = chargeEnd < presenceEnd ? chargeEnd : presenceEnd;
                var chargingHoursInSlot = Math.Max(0, (chargeStop - chargeStart).TotalHours);
                var energyThisSlotKwh = maxVehicleKw * chargingHoursInSlot;

                if (!slots.TryGetValue(cursor, out var accumulator))
                {
                    accumulator = new PowerAccumulator();
                    slots[cursor] = accumulator;
                }

                accumulator.Vehicles.Add(powerEvent.VehicleKey);
                accumulator.Events++;
                // Average power over the 1-hour slot: energy delivered / 1 h.
                accumulator.RequiredKw += energyThisSlotKwh;
                if (includeVehicleDemands)
                {
                    var avgPowerForSlot = energyThisSlotKwh; // per 1h slot
                    accumulator.AddVehicleDemand(powerEvent, energyThisSlotKwh, avgPowerForSlot);
                }

                cursor = next;
            }
        }

        var peakPerHour = slots
            .Select(slot => new
            {
                Hour = slot.Key.Hour,
                Vehicles = slot.Value.Vehicles.LongCount(),
                slot.Value.Events,
                slot.Value.RequiredKw,
                Date = slot.Key,
                VehicleDemands = includeVehicleDemands
                    ? slot.Value.VehicleDemands
                        .Values
                        .OrderByDescending(x => x.RequiredKw)
                        .ThenBy(x => x.Wagencode, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Kenteken, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.ToRow())
                        .ToArray()
                    : [],
            })
            .GroupBy(x => x.Hour)
            .Select(group => group.OrderByDescending(x => x.RequiredKw).ThenByDescending(x => x.Vehicles).First())
            .ToDictionary(x => x.Hour);

        return Enumerable.Range(0, 24)
            .Select(hour =>
            {
                peakPerHour.TryGetValue(hour, out var peak);
                var rawKw = peak?.RequiredKw ?? 0;
                var cappedKw = Math.Min(rawKw, siteLimitKw);
                var anomalyFlag = rawKw > siteLimitKw * 1.001;
                var requiredKw = Math.Round(cappedKw, 0);
                return new PowerHourlyCell(
                    hour,
                    $"{hour:00}:00",
                    peak?.Vehicles ?? 0,
                    peak?.Events ?? 0,
                    requiredKw,
                    Math.Round(requiredKw / 1000.0, 2),
                    peak is null ? null : DateOnly.FromDateTime(peak.Date),
                    peak?.VehicleDemands ?? [],
                    anomalyFlag);
            })
            .ToArray();
    }

    private PowerScenarioProfile[] BuildScenarioProfiles(IReadOnlyList<PowerEvent> events, PowerProfileRequest request)
    {
        var years = request.ScenarioYears.Length == 0 ? [2027, 2030] : request.ScenarioYears;
        var currentVehicles = Math.Max(1, events.Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var baseHourly = BuildHourlyPowerProfile(events, request);
        var rolloutMode = _store.Options.FleetRolloutMode;
        var k = _store.Options.FleetRolloutK;
        var t0 = _store.Options.FleetRolloutT0Year;
        var siteLimitKw = request.SiteLimitMw > 0 ? request.SiteLimitMw * 1000.0 : double.PositiveInfinity;
        return years
            .Select(year =>
            {
                var inflow = _store.Options.ScenarioInflows.FirstOrDefault(x => x.Year == year) ?? new ScenarioInflowAssumption(year, 0, 0);
                var scenarioVehicles = Math.Max(0, inflow.TractorCount + inflow.BoxTruckCount);
                var scale = Math.Round(Math.Min(1.0, scenarioVehicles / (double)currentVehicles), 4);
                var sigmoidScale = string.Equals(rolloutMode, "scurve", StringComparison.OrdinalIgnoreCase)
                    ? Math.Round(1.0 / (1.0 + Math.Exp(-k * (year - t0))), 4)
                    : (double?)null;
                if (sigmoidScale is not null)
                {
                    scale = Math.Round(Math.Min(1.0, scale * sigmoidScale.Value * 2.0), 4);
                }
                var mode = NormalizeScenarioMode(request.ScenarioMode);
                var source = mode == "cherry-pick"
                    ? BuildHourlyPowerProfile(events.OrderByDescending(x => x.DwellMin).ThenBy(x => x.StartTime).Take(scenarioVehicles).ToArray(), request)
                    : baseHourly;
                return new PowerScenarioProfile(
                    year,
                    sigmoidScale is null ? mode : mode + "+scurve",
                    scale,
                    source.Select(cell =>
                    {
                        var scaledKw = cell.RequiredKw * scale;
                        var anomaly = cell.AnomalyFlag || scaledKw > siteLimitKw * 1.001;
                        var capped = Math.Min(scaledKw, siteLimitKw);
                        return cell with
                        {
                            Vehicles = (long)Math.Ceiling(cell.Vehicles * scale),
                            Events = (long)Math.Ceiling(cell.Events * scale),
                            RequiredKw = Math.Round(capped, 0),
                            RequiredMw = Math.Round(capped / 1000.0, 2),
                            AnomalyFlag = anomaly,
                        };
                    }).ToArray());
            })
            .ToArray();
    }

    private PowerProfileRequest NormalizePowerRequest(PowerProfileRequest request)
    {
        var normalized = NormalizeFilter(request);
        return new PowerProfileRequest
        {
            DateFrom = normalized.DateFrom,
            DateTo = normalized.DateTo,
            VervoerderTypes = normalized.VervoerderTypes,
            Vervoerders = normalized.Vervoerders,
            Wagencodes = normalized.Wagencodes,
            MinDwellMin = normalized.MinDwellMin <= 0 ? 15 : normalized.MinDwellMin,
            RoadThreshold = normalized.RoadThreshold,
            RoadTopPercent = normalized.RoadTopPercent,
            MarkerTopN = normalized.MarkerTopN,
            ZeZoneMode = normalized.ZeZoneMode,
            VehicleClasses = NormalizeVehicleClasses(request.VehicleClasses),
            TopLocations = Math.Clamp(request.TopLocations, 1, 50),
            ScenarioYears = (request.ScenarioYears.Length == 0 ? [2027, 2030] : request.ScenarioYears)
                .Where(year => year >= 2026 && year <= 2040)
                .Distinct()
                .Order()
                .ToArray(),
            ScenarioMode = NormalizeScenarioMode(request.ScenarioMode),
            CapacityKwh = Math.Clamp(request.CapacityKwh, 100, 1_500),
            MaxVehicleKw = Math.Clamp(request.MaxVehicleKw <= 0 ? 400 : request.MaxVehicleKw, 50, 1_500),
            SiteLimitMw = Math.Clamp(request.SiteLimitMw <= 0 ? 1.4 : request.SiteLimitMw, 0.05, 50),
            KwhPerKm = Math.Clamp(request.KwhPerKm <= 0 ? 1.2 : request.KwhPerKm, 0.3, 3.5),
            MinSocPct = Math.Clamp(request.MinSocPct, 0, 50),
            TargetSocPct = Math.Clamp(Math.Max(request.TargetSocPct, request.MinSocPct + 1), 20, 100),
        };
    }

    private PowerLocationProfileRequest NormalizePowerLocationRequest(PowerLocationProfileRequest request)
    {
        var normalized = NormalizePowerRequest(request);
        return new PowerLocationProfileRequest
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
            VehicleClasses = normalized.VehicleClasses,
            TopLocations = normalized.TopLocations,
            ScenarioYears = normalized.ScenarioYears,
            ScenarioMode = normalized.ScenarioMode,
            CapacityKwh = normalized.CapacityKwh,
            MaxVehicleKw = normalized.MaxVehicleKw,
            SiteLimitMw = normalized.SiteLimitMw,
            KwhPerKm = normalized.KwhPerKm,
            MinSocPct = normalized.MinSocPct,
            TargetSocPct = normalized.TargetSocPct,
            LocationId = request.LocationId.Trim(),
        };
    }

    private static string BuildPowerWhere(PowerProfileRequest request, bool onlyChargeWindows)
    {
        var parts = new List<string>
        {
            "gepland_start IS NOT NULL",
            "gepland_eind IS NOT NULL",
            "gepland_eind > gepland_start",
        };

        if (onlyChargeWindows)
        {
            parts.Add($"actie_soort IN ({SqlStringList(ChargeWindowActions)})");
        }

        if (request.DateFrom is not null)
        {
            parts.Add($"CAST(trip_date AS DATE) >= DATE {DuckDbRouteStore.SqlString(request.DateFrom.Value.ToString("yyyy-MM-dd"))}");
        }

        if (request.DateTo is not null)
        {
            parts.Add($"CAST(trip_date AS DATE) <= DATE {DuckDbRouteStore.SqlString(request.DateTo.Value.ToString("yyyy-MM-dd"))}");
        }

        if (request.MinDwellMin > 0)
        {
            parts.Add($"COALESCE(CAST(dwell_min AS DOUBLE), 0) >= {request.MinDwellMin.ToString(CultureInfo.InvariantCulture)}");
        }

        var vehicleClasses = NormalizeVehicleClasses(request.VehicleClasses);
        if (vehicleClasses.Length == 0 && request.VervoerderTypes.Length > 0)
        {
            vehicleClasses = request.VervoerderTypes
                .Select(x => x.Equals("eigen", StringComparison.OrdinalIgnoreCase) ? "own" : x)
                .Select(x => x.Equals("onbekend", StringComparison.OrdinalIgnoreCase) ? "unknown" : x)
                .ToArray();
        }

        AddIn(parts, "vehicle_class", vehicleClasses);
        AddIn(parts, "vervoerder", request.Vervoerders);
        AddVehicleIn(parts, request.Wagencodes);
        return string.Join(" AND ", parts);
    }

    private string[] PowerAssumptionTexts()
    {
        var assumptions = new List<string>
        {
            "Vermogensvraag per voertuig: front-loaded laadcurve. Vermogen = MaxVehicleKw zolang energie < min(distance × kWh/km, capacity × (target-min)/100); daarna 0.",
            "Default batterijcapaciteit: 590 kWh, instelbaar via de Batterijcapaciteit-input.",
            "Default SoC-venster: laden van 15% naar 80% (= 383.5 kWh bij 590 kWh accu).",
            "Default voertuigacceptatie: 400 kW per truck; MCS-selectie verhoogt dit naar 1.500 kW.",
            "Default kWh/km: 1.2 (configurable per voertuigklasse × seizoen via RouteAnalysisDefaults).",
            "Site-limit: per-uur aggregatie wordt gecapped op SiteLimitMw (default 1.4 MW); overschrijdingen krijgen anomaly_flag = true.",
        };
        assumptions.Add("Laadvensters: wait_task_available, wait_after, wait_action, pause.");
        assumptions.Add("Instroom Madeleine: 2026=3 trekkers/1 bakwagen; 2027=10/16; 2028=21/25; 2029=38/25; 2030=56/25; 2031=75/25.");
        assumptions.Add($"Focuslocatie: {_store.Options.FocusLocationAlias}.");
        return assumptions.ToArray();
    }

    private static string[] NormalizeVehicleClasses(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Select(value => value.Trim().ToLowerInvariant())
            .Select(value => value == "eigen" ? "own" : value)
            .Select(value => value == "onbekend" ? "unknown" : value)
            .Where(value => value is "own" or "charter" or "unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeScenarioMode(string? mode)
    {
        return string.Equals(mode, "cherry-pick", StringComparison.OrdinalIgnoreCase)
            ? "cherry-pick"
            : "linear";
    }

    private static string SqlStringList(IEnumerable<string> values)
    {
        return string.Join(", ", values.Select(DuckDbRouteStore.SqlString));
    }

    private static string ModeText(IEnumerable<string> values)
    {
        return values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key)
            .FirstOrDefault() ?? "";
    }

    private static string ToHourlyCsv(PowerLocationProfile profile)
    {
        var builder = new StringBuilder("location_id,name,address,hour,vehicles,events,required_kw,required_mw,anomaly_flag\n");
        foreach (var cell in profile.HourlyProfile)
        {
            builder.AppendCsv(profile.LocationId)
                .AppendCsv(profile.Name)
                .AppendCsv(profile.Address)
                .AppendCsv(cell.Hour.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.Vehicles.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.Events.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.RequiredKw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.RequiredMw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.AnomalyFlag ? "1" : "0", endLine: true);
        }

        return builder.ToString();
    }

    private static string ToDailyCsv(IEnumerable<PowerDailyMetric> metrics)
    {
        var builder = new StringBuilder("date,unique_vehicles,own_vehicles,charter_vehicles,trips,events,avg_dwell_min,peak_kw,anomaly_flag\n");
        foreach (var row in metrics)
        {
            builder.AppendCsv(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .AppendCsv(row.UniqueVehicles.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.OwnVehicles.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.CharterVehicles.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Trips.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Events.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.AvgDwellMin.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.PeakKw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.AnomalyFlag ? "1" : "0", endLine: true);
        }

        return builder.ToString();
    }

    private static string ToScenarioCsv(IEnumerable<PowerScenarioProfile> scenarios)
    {
        var builder = new StringBuilder("year,mode,scale_factor,hour,vehicles,events,required_kw,required_mw,anomaly_flag\n");
        foreach (var scenario in scenarios)
        {
            foreach (var cell in scenario.HourlyProfile)
            {
                builder.AppendCsv(scenario.Year.ToString(CultureInfo.InvariantCulture))
                    .AppendCsv(scenario.Mode)
                    .AppendCsv(scenario.ScaleFactor.ToString(CultureInfo.InvariantCulture))
                    .AppendCsv(cell.Hour.ToString(CultureInfo.InvariantCulture))
                    .AppendCsv(cell.Vehicles.ToString(CultureInfo.InvariantCulture))
                    .AppendCsv(cell.Events.ToString(CultureInfo.InvariantCulture))
                    .AppendCsv(cell.RequiredKw.ToString(CultureInfo.InvariantCulture))
                    .AppendCsv(cell.RequiredMw.ToString(CultureInfo.InvariantCulture))
                    .AppendCsv(cell.AnomalyFlag ? "1" : "0", endLine: true);
            }
        }

        return builder.ToString();
    }

    private static string ToHeatmapCsv(IEnumerable<PowerHeatmapCell> heatmap)
    {
        var builder = new StringBuilder("location_id,location_name,hour,vehicles,required_kw,required_mw,anomaly_flag\n");
        foreach (var cell in heatmap)
        {
            builder.AppendCsv(cell.LocationId)
                .AppendCsv(cell.LocationName)
                .AppendCsv(cell.Hour.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.Vehicles.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.RequiredKw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.RequiredMw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(cell.AnomalyFlag ? "1" : "0", endLine: true);
        }

        return builder.ToString();
    }

    private static string BuildReportHtml(PowerLocationProfile profile, PowerLocationProfileResponse detail)
    {
        var max = Math.Max(1, profile.HourlyProfile.Max(x => x.RequiredKw));
        var cells = string.Join("", profile.HourlyProfile.Select(cell =>
        {
            var alpha = cell.RequiredKw <= 0 ? 0 : 0.14 + 0.76 * (cell.RequiredKw / max);
            return $"""
                <div class="cell" style="background:rgba(249,188,19,{alpha.ToString("0.###", CultureInfo.InvariantCulture)})">
                    <strong>{cell.Hour:00}</strong><span>{cell.RequiredKw:N0} kW</span>
                </div>
                """;
        }));
        var scenarios = string.Join("", detail.Scenarios.Select(s =>
        {
            var peak = s.HourlyProfile.OrderByDescending(x => x.RequiredKw).FirstOrDefault();
            return $"<tr><td>{s.Year}</td><td>{s.Mode}</td><td>{s.ScaleFactor:P0}</td><td>{peak?.Label}</td><td>{peak?.RequiredKw:N0} kW</td></tr>";
        }));
        var dailyPeaks = string.Join("", detail.DailyMetrics
            .OrderByDescending(x => x.PeakKw)
            .ThenByDescending(x => x.UniqueVehicles)
            .Take(10)
            .Select(day => $"<tr><td>{day.Date:dd-MM-yyyy}</td><td>{day.UniqueVehicles:N0}</td><td>{day.OwnVehicles:N0}</td><td>{day.CharterVehicles:N0}</td><td>{day.AvgDwellMin:N0} min</td><td>{day.PeakKw:N0} kW</td></tr>"));

        return $$"""
        <!doctype html>
        <html lang="nl">
        <head>
          <meta charset="utf-8">
          <title>Nieuwegein 24-uurs vermogensprofiel</title>
          <style>
            body{font-family:Inter,Arial,sans-serif;margin:0;padding:32px;color:#2e2343;background:#fff}
            h1{font-size:34px;margin:0 0 6px} p{color:#667085;margin:0 0 24px}
            .metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:20px 0}
            .metric{border:1px solid #dfe4ee;border-radius:8px;padding:14px}.metric span{display:block;color:#667085;font-size:13px}.metric strong{font-size:24px}
            .grid{display:grid;grid-template-columns:repeat(24,1fr);gap:4px;margin-top:18px}
            .cell{min-height:68px;border:1px solid #d8dce6;border-radius:6px;padding:7px;display:grid;align-content:space-between}
            .cell strong{font-size:12px}.cell span{font-weight:800;font-size:13px}
            table{width:100%;border-collapse:collapse;margin-top:24px}td,th{border-bottom:1px solid #e6e9f0;text-align:left;padding:9px}
          </style>
        </head>
        <body>
          <h1>Nieuwegein 24-uurs vermogensprofiel</h1>
          <p>{{EscapeHtml(profile.Address)}} · piek per uur over geselecteerde periode</p>
          <section class="metrics">
            <div class="metric"><span>Voertuigen</span><strong>{{profile.UniqueVehicles:N0}}</strong></div>
            <div class="metric"><span>Eigen</span><strong>{{profile.UniqueOwnVehicles:N0}}</strong></div>
            <div class="metric"><span>Laadvensters</span><strong>{{profile.Events:N0}}</strong></div>
            <div class="metric"><span>Piek</span><strong>{{profile.PeakKw:N0}} kW</strong></div>
          </section>
          <section class="grid">{{cells}}</section>
          <table><thead><tr><th>Jaar</th><th>Modus</th><th>Schaal</th><th>Piekuur</th><th>Piek</th></tr></thead><tbody>{{scenarios}}</tbody></table>
          <h2>Top dagpieken</h2>
          <table><thead><tr><th>Datum</th><th>Voertuigen</th><th>Eigen</th><th>Charter</th><th>Gem. stilstand</th><th>Piek</th></tr></thead><tbody>{{dailyPeaks}}</tbody></table>
        </body>
        </html>
        """;
    }

    private async Task WriteHeatmapParquetAsync(IReadOnlyList<PowerHeatmapCell> heatmap, string parquetPath, CancellationToken cancellationToken)
    {
        var csvPath = Path.ChangeExtension(parquetPath, ".tmp.csv");
        await File.WriteAllTextAsync(csvPath, ToHeatmapCsv(heatmap), cancellationToken);
        try
        {
            using var connection = new DuckDBConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"COPY (SELECT * FROM read_csv({DuckDbRouteStore.SqlString(csvPath)}, header = true, all_varchar = false)) TO {DuckDbRouteStore.SqlString(parquetPath)} (FORMAT PARQUET);";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (File.Exists(csvPath))
            {
                File.Delete(csvPath);
            }
        }
    }

    private static async Task WriteMetaAsync(string companionFilePath, ExportMetadata meta, CancellationToken cancellationToken)
    {
        var sourceHashes = TryComputeInputHashes(meta);
        var enriched = new
        {
            generated_at_utc = meta.GeneratedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            software_version = meta.SoftwareVersion,
            companion_file = Path.GetFileName(companionFilePath),
            companion_sha256 = SafeFileSha256(companionFilePath),
            focus_location_id = meta.FocusLocationId,
            focus_location_name = meta.FocusLocationName,
            focus_address = meta.FocusAddress,
            params_used = new
            {
                capacity_kwh = meta.CapacityKwh,
                max_vehicle_kw = meta.MaxVehicleKw,
                site_limit_mw = meta.SiteLimitMw,
                scenario_years = meta.ScenarioYears,
                vehicle_classes = meta.VehicleClasses,
                fleet_rollout_mode = meta.FleetRolloutMode,
            },
            energy_assumptions = meta.EnergyAssumptions,
            scenario_inflows = meta.ScenarioInflows,
            input_source_hashes = sourceHashes,
            note = "Sidecar metadata for reviewer audit. Anomaly_flag column flags hours where raw aggregate exceeded site_limit_mw (capped on export).",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(enriched, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(companionFilePath + ".meta.json", json, cancellationToken);
    }

    private static string SafeFileSha256(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static IReadOnlyDictionary<string, string> TryComputeInputHashes(ExportMetadata meta)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ExportMetadata(
        DateTime GeneratedAtUtc,
        string SoftwareVersion,
        string FocusLocationId,
        string FocusLocationName,
        string FocusAddress,
        double CapacityKwh,
        double MaxVehicleKw,
        double SiteLimitMw,
        int[] ScenarioYears,
        string[] VehicleClasses,
        Models.VehicleEnergyAssumption[] EnergyAssumptions,
        string FleetRolloutMode,
        ScenarioInflowAssumption[] ScenarioInflows);

    private static string EscapeHtml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private sealed record PowerEvent(
        string Wagencode,
        string VehicleKey,
        string Kenteken,
        string VehicleClass,
        DateOnly TripDate,
        string TripId,
        int ActionSeq,
        string ActionType,
        string LocationId,
        string LocationName,
        string Address,
        string VehicleType,
        double DistanceKm,
        DateTime StartTime,
        DateTime EndTime,
        double DwellMin,
        double Lat,
        double Lon);

    private sealed class PowerAccumulator
    {
        public HashSet<string> Vehicles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PowerVehicleDemandAccumulator> VehicleDemands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Events { get; set; }
        public double RequiredKw { get; set; }

        public void AddVehicleDemand(PowerEvent powerEvent, double demandKwh, double requiredKw)
        {
            var key = string.IsNullOrWhiteSpace(powerEvent.VehicleKey)
                ? $"{powerEvent.Wagencode}|{powerEvent.Kenteken}|{powerEvent.StartTime:O}"
                : powerEvent.VehicleKey;
            if (!VehicleDemands.TryGetValue(key, out var vehicle))
            {
                vehicle = new PowerVehicleDemandAccumulator(
                    string.IsNullOrWhiteSpace(powerEvent.Wagencode) ? "-" : powerEvent.Wagencode,
                    string.IsNullOrWhiteSpace(powerEvent.Kenteken) ? "-" : powerEvent.Kenteken,
                    string.IsNullOrWhiteSpace(powerEvent.VehicleClass) ? "unknown" : powerEvent.VehicleClass);
                VehicleDemands[key] = vehicle;
            }

            vehicle.DemandKwh += demandKwh;
            vehicle.RequiredKw += requiredKw;
            vehicle.StandingHours = Math.Max(vehicle.StandingHours, (powerEvent.EndTime - powerEvent.StartTime).TotalHours);
            if (vehicle.StartTime == DateTime.MinValue || powerEvent.StartTime < vehicle.StartTime)
            {
                vehicle.StartTime = powerEvent.StartTime;
            }

            if (powerEvent.EndTime > vehicle.EndTime)
            {
                vehicle.EndTime = powerEvent.EndTime;
            }
        }
    }

    private sealed class PowerVehicleDemandAccumulator(string wagencode, string kenteken, string vehicleClass)
    {
        public string Wagencode { get; } = wagencode;
        public string Kenteken { get; } = kenteken;
        public string VehicleClass { get; } = vehicleClass;
        public double DemandKwh { get; set; }
        public double RequiredKw { get; set; }
        public double StandingHours { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public PowerHourlyVehicle ToRow()
        {
            var window = StartTime == DateTime.MinValue || EndTime == DateTime.MinValue
                ? ""
                : $"{StartTime:HH:mm}-{EndTime:HH:mm}";
            return new PowerHourlyVehicle(
                Wagencode,
                Kenteken,
                VehicleClass,
                Math.Round(DemandKwh, 0),
                Math.Round(RequiredKw, 0),
                Math.Round(StandingHours, 3),
                window);
        }
    }
}

internal static class PowerProfileCsvExtensions
{
    public static StringBuilder AppendCsv(this StringBuilder builder, string value, bool endLine = false)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append(',');
        }

        builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        if (endLine)
        {
            builder.AppendLine();
        }

        return builder;
    }
}
