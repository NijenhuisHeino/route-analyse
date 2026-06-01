# Pauze-Laadvraag Wegvlakken Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a new road-layer analysis that estimates public charging demand on highway road segments when trucks reach the 3.5-4.5 hour driving window in a shift.

**Architecture:** Add a focused `RoadBreakDemandService.cs` partial for calculation and keep existing `RouteAnalysisService.cs` concerns intact. Add request/response models and API endpoints, then wire the map and Home UI as a separate layer so existing road passage analysis remains available. Use low/medium/high route-quality flags and visible diagnostics because the route-to-road matching may be approximate when only start/end trip geometry is available.

**Tech Stack:** .NET 10 Blazor Server, DuckDB.NET, MapLibre via `wwwroot/plannerMap.js`, xUnit service/API tests.

---

## File Structure

- Modify `src/LaadinfrastructuurPlanner/Models/RouteAnalysisModels.cs`: add request/response records for break-demand map and detail.
- Create `src/LaadinfrastructuurPlanner/Services/RoadBreakDemandService.cs`: shift construction, reset-location detection, break-window matching, aggregation, and detail selection.
- Modify `src/LaadinfrastructuurPlanner/Endpoints/PlannerApiEndpoints.cs`: add `/api/roads/break-demand` and `/api/roads/break-demand/detail`.
- Modify `src/LaadinfrastructuurPlanner/Components/Pages/Home.razor`: add controls, layer toggle, request builders, and a road-break detail panel.
- Modify `src/LaadinfrastructuurPlanner/wwwroot/plannerMap.js`: add break-demand source/layers and selection callback.
- Modify `src/LaadinfrastructuurPlanner/wwwroot/app.css`: add compact UI styling for break-demand controls/profile/table.
- Modify `tests/LaadinfrastructuurPlanner.Tests/TestParquetData.cs`: extend synthetic data with trips that exercise shift resets and break windows.
- Modify `tests/LaadinfrastructuurPlanner.Tests/RouteAnalysisServiceTests.cs`: add service tests for shift logic, kW math, and aggregation.
- Modify `tests/LaadinfrastructuurPlanner.Tests/PlannerApiEndpointTests.cs`: add endpoint coverage.

---

### Task 1: Add Break-Demand Models

**Files:**
- Modify: `src/LaadinfrastructuurPlanner/Models/RouteAnalysisModels.cs`
- Test: `tests/LaadinfrastructuurPlanner.Tests/RouteAnalysisServiceTests.cs`

- [ ] **Step 1: Write the failing compile-level service test**

Add this test at the end of `RouteAnalysisServiceTests` before `WarmApiCallsStayUnderTwoSecondsOnSyntheticCache`:

```csharp
[Fact]
public async Task RoadBreakDemandDefaultsExposeWindowAndDiagnostics()
{
    var demand = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
    {
        RoadThreshold = 1,
        KwhPerKm = 1.0
    });

    Assert.Equal("ok", demand.Status);
    Assert.Equal(3.5, demand.WindowStartHours);
    Assert.Equal(4.5, demand.WindowEndHours);
    Assert.Equal(0.75, demand.BreakDurationHours);
    Assert.True(demand.Diagnostics.TotalTrips >= 0);
    Assert.NotNull(demand.Lines);
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test tests/LaadinfrastructuurPlanner.Tests/LaadinfrastructuurPlanner.Tests.csproj --filter RoadBreakDemandDefaultsExposeWindowAndDiagnostics
```

Expected: compile failure because `RoadBreakDemandRequest` and `GetRoadBreakDemandMapAsync` do not exist.

- [ ] **Step 3: Add the model records**

Append these records after `RoadSelectionRequest` in `RouteAnalysisModels.cs`:

```csharp
public sealed record RoadBreakDemandRequest : AnalysisFilter
{
    public double KwhPerKm { get; init; } = 1.2;
    public double WindowStartHours { get; init; } = 3.5;
    public double WindowEndHours { get; init; } = 4.5;
    public double BreakDurationHours { get; init; } = 0.75;
    public double ShiftResetGapHours { get; init; } = 2.0;
    public double ResetLocationRadiusKm { get; init; } = 0.75;
}

public sealed record RoadBreakDemandDetailRequest : RoadBreakDemandRequest
{
    public RoadSelection Road { get; init; } = new(0, 0, 0, 0);
}

public sealed record RoadBreakDemandMapResponse(
    string Status,
    string? Message,
    double WindowStartHours,
    double WindowEndHours,
    double BreakDurationHours,
    RoadBreakDemandLine[] Lines,
    RoadBreakDemandDiagnostics Diagnostics,
    bool FromCache);

public sealed record RoadBreakDemandLine(
    double Lat1,
    double Lon1,
    double Lat2,
    double Lon2,
    string SegmentId,
    string Direction,
    double PeakMw,
    double TotalKwh,
    long Vehicles,
    long Passages,
    string RouteQuality,
    double SelectionRadiusKm,
    RoadPoint[]? Coordinates = null);

public sealed record RoadBreakDemandDiagnostics(
    long TotalTrips,
    long IncludedPassages,
    long ExcludedTrips,
    long LowQualityMatches,
    string[] ExclusionReasons);

public sealed record RoadBreakDemandDetailResponse(
    string Status,
    string? Message,
    string Title,
    double PeakMw,
    double TotalKwh,
    long Vehicles,
    long Passages,
    RoadBreakQuarterCell[] QuarterProfile,
    RoadBreakVehicleRow[] VehiclesInWindow,
    RoadBreakDemandDiagnostics Diagnostics);

public sealed record RoadBreakQuarterCell(
    DateTime SlotStart,
    string Label,
    long Vehicles,
    double RequiredKw,
    double RequiredMw,
    double DemandKwh);

public sealed record RoadBreakVehicleRow(
    string Wagencode,
    string Kenteken,
    DateTime ShiftStart,
    DateTime BreakStart,
    double DriveHoursSinceShiftStart,
    double KmSinceShiftStart,
    double DemandKwh,
    double RequiredKw,
    string RouteQuality);
```

- [ ] **Step 4: Add a minimal compiling service stub**

Create `src/LaadinfrastructuurPlanner/Services/RoadBreakDemandService.cs`:

```csharp
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    public Task<RoadBreakDemandMapResponse> GetRoadBreakDemandMapAsync(
        RoadBreakDemandRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRoadBreakDemandRequest(request);
        return Task.FromResult(new RoadBreakDemandMapResponse(
            "ok",
            null,
            normalized.WindowStartHours,
            normalized.WindowEndHours,
            normalized.BreakDurationHours,
            [],
            new RoadBreakDemandDiagnostics(0, 0, 0, 0, []),
            true));
    }

    private static RoadBreakDemandRequest NormalizeRoadBreakDemandRequest(RoadBreakDemandRequest request)
    {
        var start = Math.Clamp(request.WindowStartHours, 0.5, 12);
        var end = Math.Clamp(request.WindowEndHours, start + 0.25, 14);
        return request with
        {
            KwhPerKm = Math.Clamp(request.KwhPerKm, 0.1, 5),
            WindowStartHours = start,
            WindowEndHours = end,
            BreakDurationHours = Math.Clamp(request.BreakDurationHours, 0.25, 3),
            ShiftResetGapHours = Math.Clamp(request.ShiftResetGapHours, 0.5, 12),
            ResetLocationRadiusKm = Math.Clamp(request.ResetLocationRadiusKm, 0.1, 5)
        };
    }
}
```

- [ ] **Step 5: Run the test and verify it passes**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test tests/LaadinfrastructuurPlanner.Tests/LaadinfrastructuurPlanner.Tests.csproj --filter RoadBreakDemandDefaultsExposeWindowAndDiagnostics
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LaadinfrastructuurPlanner/Models/RouteAnalysisModels.cs src/LaadinfrastructuurPlanner/Services/RoadBreakDemandService.cs tests/LaadinfrastructuurPlanner.Tests/RouteAnalysisServiceTests.cs
git commit -m "feat: add road break demand contracts"
```

---

### Task 2: Add Synthetic Shift Test Data

**Files:**
- Modify: `tests/LaadinfrastructuurPlanner.Tests/TestParquetData.cs`
- Test: `tests/LaadinfrastructuurPlanner.Tests/RouteAnalysisServiceTests.cs`

- [ ] **Step 1: Write failing tests for shift carry-over and reset**

Add these tests to `RouteAnalysisServiceTests`:

```csharp
[Fact]
public async Task RoadBreakDemandCarriesDriveTimeAcrossTripsInSameShift()
{
    var demand = await _service.GetRoadBreakDemandMapAsync(new RoadBreakDemandRequest
    {
        RoadThreshold = 1,
        KwhPerKm = 1.0,
        WindowStartHours = 3.5,
        WindowEndHours = 4.5,
        BreakDurationHours = 0.75,
        ShiftResetGapHours = 2.0
    });

    Assert.Contains(demand.Lines, line => line.Passages > 0 && line.TotalKwh > 0 && line.PeakMw > 0);
    Assert.Contains(demand.Diagnostics.ExclusionReasons, reason => reason.Contains("included", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public async Task RoadBreakDemandResetsOnlyAfterLongGapAtResetLocation()
{
    var detail = await _service.GetRoadBreakDemandDetailAsync(new RoadBreakDemandDetailRequest
    {
        Road = new RoadSelection(52.0, 5.0, 52.02, 5.02, 100),
        KwhPerKm = 1.0,
        ShiftResetGapHours = 2.0
    });

    Assert.Equal("ok", detail.Status);
    Assert.All(detail.VehiclesInWindow, row => Assert.InRange(row.DriveHoursSinceShiftStart, 3.5, 4.5));
    Assert.DoesNotContain(detail.VehiclesInWindow, row => row.Wagencode == "W3" && row.KmSinceShiftStart > 500);
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test tests/LaadinfrastructuurPlanner.Tests/LaadinfrastructuurPlanner.Tests.csproj --filter "RoadBreakDemandCarriesDriveTimeAcrossTripsInSameShift|RoadBreakDemandResetsOnlyAfterLongGapAtResetLocation"
```

Expected: compile failure for `GetRoadBreakDemandDetailAsync` or assertion failure because map lines are still empty.

- [ ] **Step 3: Extend synthetic stop data**

In `WriteStopsParquet`, add rows for vehicle `W3` and `W4`:

```sql
('W3', 'Eigen vervoer', 'eigen', DATE '2026-01-03', 'T5', 0, 'Origin', 'Depot A', 'Adres A 1234 AB', TIMESTAMP '2026-01-03 06:00:00', TIMESTAMP '2026-01-03 06:00:00', 0.0, 100.0, 5.0, 52.000, 5.000),
('W3', 'Eigen vervoer', 'eigen', DATE '2026-01-03', 'T5', 1, 'Destination', 'Hub B', 'Adres B', TIMESTAMP '2026-01-03 08:00:00', TIMESTAMP '2026-01-03 08:00:00', 100.0, 100.0, 15.0, 52.010, 5.010),
('W3', 'Eigen vervoer', 'eigen', DATE '2026-01-03', 'T6', 0, 'Origin', 'Hub B', 'Adres B', TIMESTAMP '2026-01-03 08:20:00', TIMESTAMP '2026-01-03 08:20:00', 0.0, 110.0, 5.0, 52.010, 5.010),
('W3', 'Eigen vervoer', 'eigen', DATE '2026-01-03', 'T6', 1, 'Destination', 'Depot A', 'Adres A 1234 AB', TIMESTAMP '2026-01-03 10:30:00', TIMESTAMP '2026-01-03 10:30:00', 110.0, 110.0, 15.0, 52.020, 5.020),
('W3', 'Eigen vervoer', 'eigen', DATE '2026-01-03', 'T7', 0, 'Origin', 'Depot A', 'Adres A 1234 AB', TIMESTAMP '2026-01-03 13:00:00', TIMESTAMP '2026-01-03 13:00:00', 0.0, 90.0, 5.0, 52.000, 5.000),
('W3', 'Eigen vervoer', 'eigen', DATE '2026-01-03', 'T7', 1, 'Destination', 'Hub B', 'Adres B', TIMESTAMP '2026-01-03 14:30:00', TIMESTAMP '2026-01-03 14:30:00', 90.0, 90.0, 15.0, 52.010, 5.010),
('W4', 'Charter', 'charter', DATE '2026-01-03', 'T8', 0, 'Origin', 'Klant X', 'Adres X', TIMESTAMP '2026-01-03 06:00:00', TIMESTAMP '2026-01-03 06:00:00', 0.0, 130.0, 5.0, 52.500, 5.500),
('W4', 'Charter', 'charter', DATE '2026-01-03', 'T8', 1, 'Destination', 'Klant Y', 'Adres Y', TIMESTAMP '2026-01-03 08:00:00', TIMESTAMP '2026-01-03 08:00:00', 130.0, 130.0, 15.0, 52.600, 5.600),
('W4', 'Charter', 'charter', DATE '2026-01-03', 'T9', 0, 'Origin', 'Klant Y', 'Adres Y', TIMESTAMP '2026-01-03 10:30:00', TIMESTAMP '2026-01-03 10:30:00', 0.0, 140.0, 5.0, 52.600, 5.600),
('W4', 'Charter', 'charter', DATE '2026-01-03', 'T9', 1, 'Destination', 'Depot C', 'Adres C', TIMESTAMP '2026-01-03 13:00:00', TIMESTAMP '2026-01-03 13:00:00', 140.0, 140.0, 15.0, 53.000, 6.000)
```

Keep the final `AS t(...)` column list unchanged.

- [ ] **Step 4: Add road parquet rows that can match the synthetic routes**

In `WriteRoadParquets`, add edges:

```sql
(52.000, 5.000, 52.020, 5.020, 1, 2),
(52.500, 5.500, 52.600, 5.600, 1, 2)
```

- [ ] **Step 5: Run the tests again**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test tests/LaadinfrastructuurPlanner.Tests/LaadinfrastructuurPlanner.Tests.csproj --filter "RoadBreakDemandCarriesDriveTimeAcrossTripsInSameShift|RoadBreakDemandResetsOnlyAfterLongGapAtResetLocation"
```

Expected: still failing until calculation is implemented, but synthetic data compiles and loads.

- [ ] **Step 6: Commit**

```bash
git add tests/LaadinfrastructuurPlanner.Tests/TestParquetData.cs tests/LaadinfrastructuurPlanner.Tests/RouteAnalysisServiceTests.cs
git commit -m "test: add road break demand shift scenarios"
```

---

### Task 3: Implement Shift Construction And Break Events

**Files:**
- Modify: `src/LaadinfrastructuurPlanner/Services/RoadBreakDemandService.cs`
- Test: `tests/LaadinfrastructuurPlanner.Tests/RouteAnalysisServiceTests.cs`

- [ ] **Step 1: Implement trip query, reset locations, and event generation**

Replace the stub in `RoadBreakDemandService.cs` with:

```csharp
using System.Globalization;
using DuckDB.NET.Data;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    private const double MinReasonableSpeedKmh = 5;
    private const double MaxReasonableSpeedKmh = 95;

    public async Task<RoadBreakDemandMapResponse> GetRoadBreakDemandMapAsync(
        RoadBreakDemandRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        var normalized = NormalizeRoadBreakDemandRequest(request);
        var events = await BuildRoadBreakEventsAsync(normalized, cancellationToken);
        var lines = AggregateBreakDemandLines(events, normalized);
        return new RoadBreakDemandMapResponse(
            "ok",
            null,
            normalized.WindowStartHours,
            normalized.WindowEndHours,
            normalized.BreakDurationHours,
            lines,
            BuildBreakDiagnostics(events),
            true);
    }

    public async Task<RoadBreakDemandDetailResponse> GetRoadBreakDemandDetailAsync(
        RoadBreakDemandDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        var normalized = NormalizeRoadBreakDemandRequest(request);
        var events = await BuildRoadBreakEventsAsync(normalized, cancellationToken);
        var road = request.Road;
        var midLat = (road.Lat1 + road.Lat2) / 2.0;
        var midLon = (road.Lon1 + road.Lon2) / 2.0;
        var radius = Math.Clamp(road.RadiusKm, 0.2, 25);
        var selected = events
            .Where(x => DistanceKm(x.MidLat, x.MidLon, midLat, midLon) <= radius)
            .ToArray();
        var profile = BuildQuarterProfile(selected, normalized);
        return new RoadBreakDemandDetailResponse(
            "ok",
            selected.Length == 0 ? "Geen pauze-laadvraag gevonden voor dit wegvlak." : null,
            $"Pauze-laadvraag wegvlak · {midLat:0.000}, {midLon:0.000}",
            profile.Select(x => x.RequiredMw).DefaultIfEmpty(0).Max(),
            Math.Round(selected.Sum(x => x.DemandKwh), 1),
            selected.Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
            selected.LongLength,
            profile,
            selected
                .OrderByDescending(x => x.RequiredKw)
                .ThenBy(x => x.BreakStart)
                .Take(100)
                .Select(x => new RoadBreakVehicleRow(x.Wagencode, x.Kenteken, x.ShiftStart, x.BreakStart, Math.Round(x.DriveHoursSinceShiftStart, 2), Math.Round(x.KmSinceShiftStart, 1), Math.Round(x.DemandKwh, 1), Math.Round(x.RequiredKw, 0), x.RouteQuality))
                .ToArray(),
            BuildBreakDiagnostics(events));
    }

    private async Task<RoadBreakEvent[]> BuildRoadBreakEventsAsync(RoadBreakDemandRequest request, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        var trips = await QueryListAsync(connection, BuildRoadBreakTripSql(request), ReadRoadBreakTrip, cancellationToken);
        var resetLocations = BuildResetLocations(trips);
        return BuildRoadBreakEvents(trips, resetLocations, request).ToArray();
    }
}
```

Add private records and helpers below this block:

```csharp
private sealed record RoadBreakTrip(string Wagencode, string Kenteken, DateOnly TripDate, string TripId, DateTime TripStart, DateTime TripEnd, double StartLat, double StartLon, double EndLat, double EndLon, double DistanceKm);

private sealed record RoadBreakEvent(string Wagencode, string Kenteken, string VehicleKey, DateTime ShiftStart, DateTime BreakStart, double DriveHoursSinceShiftStart, double KmSinceShiftStart, double DemandKwh, double RequiredKw, double MidLat, double MidLon, string RouteQuality, string ExclusionReason);
```

Use the existing `QueryListAsync`, `OpenConnection`, `GetString`, `GetDouble`, and `GetDateTime` patterns from `ChargingDemandService.cs`.

- [ ] **Step 2: Add deterministic SQL and core helpers**

Add these methods:

```csharp
private static string BuildRoadBreakTripSql(RoadBreakDemandRequest request)
{
    var parts = new List<string> { "trip_start IS NOT NULL", "trip_end IS NOT NULL", "distance_km > 0" };
    AddBreakDateRange(parts, request.DateFrom, request.DateTo, "trip_date");
    AddBreakVehicleTypeIn(parts, request.VervoerderTypes);
    AddBreakVervoerderIn(parts, request.Vervoerders);
    AddBreakVehicleIn(parts, request.Wagencodes);
    var where = string.Join(" AND ", parts);
    return $$"""
        SELECT
            CAST(wagencode AS VARCHAR) AS wagencode,
            COALESCE(CAST(kenteken AS VARCHAR), '') AS kenteken,
            CAST(trip_date AS DATE) AS trip_date,
            CAST(trip_id AS VARCHAR) AS trip_id,
            CAST(trip_start AS TIMESTAMP) AS trip_start,
            CAST(trip_end AS TIMESTAMP) AS trip_end,
            CAST(start_lat AS DOUBLE) AS start_lat,
            CAST(start_lon AS DOUBLE) AS start_lon,
            CAST(end_lat AS DOUBLE) AS end_lat,
            CAST(end_lon AS DOUBLE) AS end_lon,
            CAST(distance_km AS DOUBLE) AS distance_km
        FROM daily_trips
        WHERE {{where}}
        ORDER BY wagencode, trip_start, trip_id;
        """;
}

private static RoadBreakTrip ReadRoadBreakTrip(DuckDBDataReader reader) => new(
    GetString(reader, "wagencode"),
    GetString(reader, "kenteken"),
    DateOnly.FromDateTime(GetDateTime(reader, "trip_date")),
    GetString(reader, "trip_id"),
    GetDateTime(reader, "trip_start"),
    GetDateTime(reader, "trip_end"),
    GetDouble(reader, "start_lat"),
    GetDouble(reader, "start_lon"),
    GetDouble(reader, "end_lat"),
    GetDouble(reader, "end_lon"),
    Math.Max(0, GetDouble(reader, "distance_km")));
```

Add these local filter helpers in `RoadBreakDemandService.cs` so the feature is independent of helper signatures in other partial files:

```csharp
private static void AddBreakDateRange(List<string> parts, DateOnly? from, DateOnly? to, string column)
{
    if (from is not null) parts.Add($"{column} >= DATE '{from:yyyy-MM-dd}'");
    if (to is not null) parts.Add($"{column} <= DATE '{to:yyyy-MM-dd}'");
}

private static void AddBreakVehicleTypeIn(List<string> parts, IReadOnlyCollection<string> values)
{
    var normalized = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => SqlString(x.ToLowerInvariant())).ToArray();
    if (normalized.Length > 0) parts.Add($"LOWER(vervoerder_type) IN ({string.Join(", ", normalized)})");
}

private static void AddBreakVervoerderIn(List<string> parts, IReadOnlyCollection<string> values)
{
    var normalized = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(SqlString).ToArray();
    if (normalized.Length > 0) parts.Add($"vervoerder IN ({string.Join(", ", normalized)})");
}

private static void AddBreakVehicleIn(List<string> parts, IReadOnlyCollection<string> values)
{
    var normalized = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(SqlString).ToArray();
    if (normalized.Length > 0) parts.Add($"wagencode IN ({string.Join(", ", normalized)})");
}
```

Use those helpers in `BuildRoadBreakTripSql`:

```csharp
AddBreakDateRange(parts, request.DateFrom, request.DateTo, "trip_date");
AddBreakVehicleTypeIn(parts, request.VervoerderTypes);
AddBreakVervoerderIn(parts, request.Vervoerders);
AddBreakVehicleIn(parts, request.Wagencodes);
```

- [ ] **Step 3: Implement event generation**

Add:

```csharp
private static IEnumerable<RoadBreakEvent> BuildRoadBreakEvents(IReadOnlyList<RoadBreakTrip> trips, IReadOnlyList<ResetLocation> resetLocations, RoadBreakDemandRequest request)
{
    foreach (var group in trips.GroupBy(x => x.Wagencode, StringComparer.OrdinalIgnoreCase))
    {
        DateTime? shiftStart = null;
        DateTime? previousEnd = null;
        double previousEndLat = 0;
        double previousEndLon = 0;
        double driveHours = 0;
        double km = 0;

        foreach (var trip in group.OrderBy(x => x.TripStart))
        {
            if (shiftStart is null || ShouldResetShift(previousEnd, previousEndLat, previousEndLon, trip, resetLocations, request))
            {
                shiftStart = trip.TripStart;
                driveHours = 0;
                km = 0;
            }

            var durationHours = Math.Max(0, (trip.TripEnd - trip.TripStart).TotalHours);
            var speed = durationHours <= 0 ? 0 : trip.DistanceKm / durationHours;
            if (durationHours <= 0 || trip.DistanceKm <= 0 || speed < MinReasonableSpeedKmh || speed > MaxReasonableSpeedKmh)
            {
                previousEnd = trip.TripEnd;
                previousEndLat = trip.EndLat;
                previousEndLon = trip.EndLon;
                continue;
            }

            var tripStartDriveHours = driveHours;
            var tripEndDriveHours = driveHours + durationHours;
            var overlapStart = Math.Max(request.WindowStartHours, tripStartDriveHours);
            var overlapEnd = Math.Min(request.WindowEndHours, tripEndDriveHours);
            if (overlapStart < overlapEnd)
            {
                var progress = (overlapStart - tripStartDriveHours) / durationHours;
                var kmSinceShiftStart = km + trip.DistanceKm * progress;
                var demandKwh = kmSinceShiftStart * request.KwhPerKm;
                var requiredKw = demandKwh / request.BreakDurationHours;
                var breakStart = trip.TripStart.AddHours(durationHours * progress);
                yield return new RoadBreakEvent(
                    trip.Wagencode,
                    string.IsNullOrWhiteSpace(trip.Kenteken) ? "-" : trip.Kenteken,
                    string.IsNullOrWhiteSpace(trip.Kenteken) ? trip.Wagencode : trip.Kenteken,
                    shiftStart.Value,
                    breakStart,
                    overlapStart,
                    kmSinceShiftStart,
                    demandKwh,
                    requiredKw,
                    Lerp(trip.StartLat, trip.EndLat, progress),
                    Lerp(trip.StartLon, trip.EndLon, progress),
                    "laag: lineaire ritprogressie",
                    "included");
            }

            driveHours = tripEndDriveHours;
            km += trip.DistanceKm;
            previousEnd = trip.TripEnd;
            previousEndLat = trip.EndLat;
            previousEndLon = trip.EndLon;
        }
    }
}
```

Add `Lerp`, `DistanceKm`, `ShouldResetShift`, `ResetLocation`, and `BuildResetLocations` as private helpers. `BuildResetLocations` groups trip starts and ends rounded to 3 decimals. Use thresholds `5` unique vehicles or `20` events when the input has at least `30` trips. Use thresholds `1` unique vehicle or `2` events when the input has fewer than `30` trips so the synthetic tests can exercise reset behavior.

- [ ] **Step 4: Implement line aggregation and quarter profile**

Add:

```csharp
private static RoadBreakDemandLine[] AggregateBreakDemandLines(IReadOnlyList<RoadBreakEvent> events, RoadBreakDemandRequest request)
{
    return events
        .Where(x => x.ExclusionReason == "included")
        .GroupBy(x => $"{Math.Round(x.MidLat, 2):0.00}:{Math.Round(x.MidLon, 2):0.00}")
        .Select(group =>
        {
            var peakKw = BuildQuarterProfile(group.ToArray(), request).Select(x => x.RequiredKw).DefaultIfEmpty(0).Max();
            var lat = group.Average(x => x.MidLat);
            var lon = group.Average(x => x.MidLon);
            return new RoadBreakDemandLine(
                lat,
                lon,
                lat + 0.001,
                lon + 0.001,
                $"break:{Math.Round(lat, 3):0.000}:{Math.Round(lon, 3):0.000}",
                "pauze-laadvraag",
                Math.Round(peakKw / 1000.0, 3),
                Math.Round(group.Sum(x => x.DemandKwh), 1),
                group.Select(x => x.VehicleKey).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                group.LongCount(),
                group.Any(x => x.RouteQuality.StartsWith("laag", StringComparison.OrdinalIgnoreCase)) ? "laag" : "hoog",
                3,
                [new RoadPoint(lat, lon), new RoadPoint(lat + 0.001, lon + 0.001)]);
        })
        .OrderByDescending(x => x.PeakMw)
        .Take(1000)
        .ToArray();
}
```

`BuildQuarterProfile` should round `BreakStart` down to the nearest 15 minutes, add the same event to 3 consecutive quarter slots for the 45-minute break duration, and sum `RequiredKw`.

- [ ] **Step 5: Run the focused tests**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test tests/LaadinfrastructuurPlanner.Tests/LaadinfrastructuurPlanner.Tests.csproj --filter "RoadBreakDemand"
```

Expected: PASS for the break-demand service tests.

- [ ] **Step 6: Commit**

```bash
git add src/LaadinfrastructuurPlanner/Services/RoadBreakDemandService.cs tests/LaadinfrastructuurPlanner.Tests/RouteAnalysisServiceTests.cs
git commit -m "feat: calculate road break demand"
```

---

### Task 4: Add API Endpoints

**Files:**
- Modify: `src/LaadinfrastructuurPlanner/Endpoints/PlannerApiEndpoints.cs`
- Test: `tests/LaadinfrastructuurPlanner.Tests/PlannerApiEndpointTests.cs`

- [ ] **Step 1: Add failing endpoint assertions**

In `PlannerApiEndpointTests`, after the existing road-detail assertions, add:

```csharp
var breakDemand = await PostAsync<RoadBreakDemandMapResponse>(client, "/api/roads/break-demand", new RoadBreakDemandRequest
{
    RoadThreshold = 1,
    KwhPerKm = 1.0
});
Assert.Equal("ok", breakDemand.Status);
Assert.NotNull(breakDemand.Diagnostics);

var breakDetail = await PostAsync<RoadBreakDemandDetailResponse>(client, "/api/roads/break-demand/detail", new RoadBreakDemandDetailRequest
{
    Road = new RoadSelection(52.0, 5.0, 52.02, 5.02, 100),
    KwhPerKm = 1.0
});
Assert.Equal("ok", breakDetail.Status);
Assert.NotNull(breakDetail.QuarterProfile);
```

- [ ] **Step 2: Run endpoint test and verify it fails**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test tests/LaadinfrastructuurPlanner.Tests/LaadinfrastructuurPlanner.Tests.csproj --filter PlannerApiEndpointsReturnExpectedJson
```

Expected: 404 for the new endpoints.

- [ ] **Step 3: Add endpoints**

In `PlannerApiEndpoints.cs`, after `/roads/selection`, add:

```csharp
api.MapPost("/roads/break-demand", ([FromBody] RoadBreakDemandRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
    service.GetRoadBreakDemandMapAsync(request, cancellationToken));

api.MapPost("/roads/break-demand/detail", ([FromBody] RoadBreakDemandDetailRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
    service.GetRoadBreakDemandDetailAsync(request, cancellationToken));
```

- [ ] **Step 4: Run endpoint test and verify it passes**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test tests/LaadinfrastructuurPlanner.Tests/LaadinfrastructuurPlanner.Tests.csproj --filter PlannerApiEndpointsReturnExpectedJson
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LaadinfrastructuurPlanner/Endpoints/PlannerApiEndpoints.cs tests/LaadinfrastructuurPlanner.Tests/PlannerApiEndpointTests.cs
git commit -m "feat: expose road break demand api"
```

---

### Task 5: Add Map Layer Support

**Files:**
- Modify: `src/LaadinfrastructuurPlanner/wwwroot/plannerMap.js`
- Test: browser verification after Task 6

- [ ] **Step 1: Add break-demand collection helper**

In `plannerMap.js`, after `lineCollection`, add:

```javascript
function breakDemandCollection(lines) {
  return {
    type: "FeatureCollection",
    features: (lines || []).map((line) => ({
      type: "Feature",
      properties: {
        segmentId: line.segmentId || "",
        peakMw: line.peakMw || 0,
        totalKwh: line.totalKwh || 0,
        vehicles: line.vehicles || 0,
        passages: line.passages || 0,
        routeQuality: line.routeQuality || "",
        radiusKm: line.selectionRadiusKm || 3
      },
      geometry: {
        type: "LineString",
        coordinates: line.coordinates?.length
          ? line.coordinates.map((point) => [point.lon, point.lat])
          : [
              [line.lon1, line.lat1],
              [line.lon2, line.lat2]
            ]
      }
    }))
  };
}
```

- [ ] **Step 2: Add sources and layers**

In `addLayers()`, add sources:

```javascript
ensureSource("road-break-demand");
ensureSource("road-break-selection");
```

Add a line layer and hitbox layer:

```javascript
if (!map.getLayer("road-break-demand")) {
  map.addLayer({
    id: "road-break-demand",
    type: "line",
    source: "road-break-demand",
    paint: {
      "line-color": ["interpolate", ["linear"], ["get", "peakMw"], 0, "#22c55e", 0.5, "#f9bc13", 2, "#dc2626"],
      "line-width": ["interpolate", ["linear"], ["get", "peakMw"], 0, 2, 2, 7],
      "line-opacity": 0.86
    },
    layout: { visibility: "none", "line-cap": "round", "line-join": "round" }
  });
}

if (!map.getLayer("road-break-hitbox")) {
  map.addLayer({
    id: "road-break-hitbox",
    type: "line",
    source: "road-break-demand",
    paint: { "line-color": "rgba(0,0,0,0)", "line-width": 18 },
    layout: { visibility: "none", "line-cap": "round", "line-join": "round" }
  });
  map.on("click", "road-break-hitbox", (e) => {
    const feature = e.features && e.features[0];
    if (!feature || !dotNetRef) return;
    const coords = feature.geometry.coordinates;
    dotNetRef.invokeMethodAsync(
      "SelectRoadBreakAsync",
      Number(coords[0][1]),
      Number(coords[0][0]),
      Number(coords[coords.length - 1][1]),
      Number(coords[coords.length - 1][0]),
      Number(feature.properties?.radiusKm || 3));
  });
}
```

- [ ] **Step 3: Fetch and display break-demand lines in update**

Inside `update`, add a `breakDemandFilter` argument before `options`. Update C# call in Task 6 to match.

Add:

```javascript
const breakDemandPromise = options.showRoadBreakDemand
  ? postJson("roads/break-demand", breakDemandFilter, signal)
  : Promise.resolve({ status: "skipped", lines: [] });
```

Include it in `Promise.all`, then:

```javascript
setData("road-break-demand", breakDemandCollection(breakDemand.lines));
setVisibility("road-break-demand", !!options.showRoadBreakDemand && breakDemand.status === "ok");
setVisibility("road-break-hitbox", !!options.showRoadBreakDemand && breakDemand.status === "ok");
if (options.showRoadBreakDemand && breakDemand.status === "ok") notes.push(`${breakDemand.lines?.length || 0} pauze-laadvraag wegvlakken`);
```

- [ ] **Step 4: Commit**

```bash
git add src/LaadinfrastructuurPlanner/wwwroot/plannerMap.js
git commit -m "feat: add road break demand map layer"
```

---

### Task 6: Add Blazor Controls And Detail Panel

**Files:**
- Modify: `src/LaadinfrastructuurPlanner/Components/Pages/Home.razor`
- Modify: `src/LaadinfrastructuurPlanner/wwwroot/app.css`

- [ ] **Step 1: Add state and controls**

In `Home.razor`, add state:

```csharp
private bool ShowRoadBreakDemand { get; set; }
private double BreakWindowStartHours { get; set; } = 3.5;
private double BreakWindowEndHours { get; set; } = 4.5;
private double BreakDurationHours { get; set; } = 0.75;
private double ShiftResetGapHours { get; set; } = 2.0;
private RoadBreakDemandDetailResponse? SelectedRoadBreakDetail { get; set; }
```

Add a layer toggle under `Wegdrukte`:

```razor
<div class="layer-toggle-item">
    <div class="layer-toggle-row">
        <label class="toggle layer-toggle-label" for="layer-pauze-laadvraag"><input id="layer-pauze-laadvraag" type="checkbox" @bind="ShowRoadBreakDemand" @bind:after="RefreshMapAsync" /> Pauze-laadvraag wegvlakken</label>
        <button type="button" class='@LayerInfoButtonClass("pauze-laadvraag")' aria-label="Uitleg over Pauze-laadvraag wegvlakken" aria-expanded='@LayerInfoExpanded("pauze-laadvraag")' aria-controls="layer-info-pauze-laadvraag" @onclick='() => ToggleLayerInfo("pauze-laadvraag")'>i</button>
    </div>
    @if (OpenLayerInfoId == "pauze-laadvraag")
    {
        <p id="layer-info-pauze-laadvraag" class="layer-info-panel">Wegvlakken waar voertuigen naar verwachting tussen 3,5 en 4,5 uur geschatte rijtijd in hun shift komen. De laag toont gevraagde publieke laadvermogenspiek, niet algemene passage-intensiteit.</p>
    }
</div>
```

Add a control group:

```razor
<section class="filter-grid break-demand-controls">
    <label>
        Rijtijd vanaf (uur)
        <input type="number" min="0.5" max="12" step="0.25" @bind="BreakWindowStartHours" @bind:after="RefreshMapAsync" />
    </label>
    <label>
        Rijtijd tot (uur)
        <input type="number" min="0.75" max="14" step="0.25" @bind="BreakWindowEndHours" @bind:after="RefreshMapAsync" />
    </label>
    <label>
        Pauzeduur (uur)
        <input type="number" min="0.25" max="3" step="0.25" @bind="BreakDurationHours" @bind:after="RefreshMapAsync" />
    </label>
    <label>
        Shift-reset gap (uur)
        <input type="number" min="0.5" max="12" step="0.25" @bind="ShiftResetGapHours" @bind:after="RefreshMapAsync" />
    </label>
    <span class="range-value">Resetlocaties: standplaatsen + vaste start/eindlocaties</span>
</section>
```

- [ ] **Step 2: Build request and map call**

Add:

```csharp
private RoadBreakDemandRequest BuildRoadBreakDemandRequest()
{
    var filter = BuildFilter();
    return new RoadBreakDemandRequest
    {
        DateFrom = filter.DateFrom,
        DateTo = filter.DateTo,
        VervoerderTypes = filter.VervoerderTypes,
        Vervoerders = filter.Vervoerders,
        Wagencodes = filter.Wagencodes,
        MinDwellMin = filter.MinDwellMin,
        RoadThreshold = filter.RoadThreshold,
        RoadTopPercent = filter.RoadTopPercent,
        MarkerTopN = filter.MarkerTopN,
        ZeZoneMode = filter.ZeZoneMode,
        KwhPerKm = ScenarioKwhPerKm,
        WindowStartHours = BreakWindowStartHours,
        WindowEndHours = BreakWindowEndHours,
        BreakDurationHours = BreakDurationHours,
        ShiftResetGapHours = ShiftResetGapHours,
        ResetLocationRadiusKm = 0.75
    };
}
```

Change `routePlannerMap.update` call to pass `BuildRoadBreakDemandRequest()` before options and add `showRoadBreakDemand = ShowRoadBreakDemand`.

- [ ] **Step 3: Add JS invokable selection**

Add:

```csharp
[JSInvokable]
public async Task SelectRoadBreakAsync(double lat1, double lon1, double lat2, double lon2, double radiusKm = 3)
{
    var request = BuildRoadBreakDemandRequest();
    SelectedRoadBreakDetail = await Analysis.GetRoadBreakDemandDetailAsync(new RoadBreakDemandDetailRequest
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
        KwhPerKm = request.KwhPerKm,
        WindowStartHours = request.WindowStartHours,
        WindowEndHours = request.WindowEndHours,
        BreakDurationHours = request.BreakDurationHours,
        ShiftResetGapHours = request.ShiftResetGapHours,
        ResetLocationRadiusKm = request.ResetLocationRadiusKm,
        Road = new RoadSelection(lat1, lon1, lat2, lon2, radiusKm)
    });
    SelectedDetail = null;
    PendingSelectionScroll = true;
    StateHasChanged();
}
```

- [ ] **Step 4: Add detail panel below map**

Render when `SelectedRoadBreakDetail is not null` near the existing `selection-detail` block:

```razor
@if (SelectedRoadBreakDetail is not null)
{
    <section id="selection-detail" class="selection-panel map-selection-detail">
        <div class="panel-heading">
            <div>
                <h3>@SelectedRoadBreakDetail.Title</h3>
                <span>Pauze-laadvraag snelweg</span>
            </div>
        </div>
        <div class="metric-row compact">
            <div class="metric"><span>Piekvraag</span><strong>@SelectedRoadBreakDetail.PeakMw.ToString("N2") MW</strong></div>
            <div class="metric"><span>kWh-vraag</span><strong>@SelectedRoadBreakDetail.TotalKwh.ToString("N0")</strong></div>
            <div class="metric"><span>Voertuigen</span><strong>@SelectedRoadBreakDetail.Vehicles.ToString("N0")</strong></div>
            <div class="metric"><span>Passages</span><strong>@SelectedRoadBreakDetail.Passages.ToString("N0")</strong></div>
        </div>
        <div class="table-panel nested-table">
            <div class="panel-heading">
                <h3>Kwartierprofiel</h3>
                <span>45 minuten laadduur telt door over drie kwartieren</span>
            </div>
            <div class="table-scroll compact-scroll">
                <table>
                    <thead><tr><th>Tijd</th><th>Voertuigen</th><th>kW</th><th>MW</th><th>kWh</th></tr></thead>
                    <tbody>
                        @foreach (var cell in SelectedRoadBreakDetail.QuarterProfile.OrderByDescending(x => x.RequiredKw).Take(24))
                        {
                            <tr><td><strong>@cell.Label</strong></td><td>@cell.Vehicles</td><td>@cell.RequiredKw.ToString("N0")</td><td>@cell.RequiredMw.ToString("N2")</td><td>@cell.DemandKwh.ToString("N0")</td></tr>
                        }
                    </tbody>
                </table>
            </div>
        </div>
    </section>
}
```

Add a second table for `VehiclesInWindow` using the columns from the design.

- [ ] **Step 5: Add CSS**

In `app.css`, add:

```css
.break-demand-controls {
  margin-top: 18px;
  padding-top: 14px;
  border-top: 1px solid var(--eta-border);
}
```

- [ ] **Step 6: Run build**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test LaadinfrastructuurPlanner.slnx
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/LaadinfrastructuurPlanner/Components/Pages/Home.razor src/LaadinfrastructuurPlanner/wwwroot/app.css
git commit -m "feat: add road break demand ui"
```

---

### Task 7: Browser Verification And Deployment

**Files:**
- No source files unless verification finds defects.

- [ ] **Step 1: Run full test suite**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test LaadinfrastructuurPlanner.slnx
```

Expected: `Passed`, 0 failures.

- [ ] **Step 2: Run local app**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet run --project src/LaadinfrastructuurPlanner --urls http://127.0.0.1:5087
```

Expected: app listens on `http://127.0.0.1:5087`.

- [ ] **Step 3: Browser-check the new layer**

Use Playwright on `http://127.0.0.1:5087/`:

```javascript
await page.evaluate(async () => {
  document.getElementById('layer-pauze-laadvraag').click();
  await new Promise(resolve => setTimeout(resolve, 3000));
  return {
    status: document.querySelector('#map-status')?.textContent,
    hasLayer: !!document.getElementById('layer-pauze-laadvraag')
  };
});
```

Expected: status mentions `pauze-laadvraag wegvlakken` and no console error is thrown.

- [ ] **Step 4: Browser-check detail data**

Use a direct API call from Playwright:

```javascript
await page.evaluate(async () => {
  const demand = await fetch('/api/roads/break-demand', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ kwhPerKm: 1.2, windowStartHours: 3.5, windowEndHours: 4.5, breakDurationHours: 0.75, shiftResetGapHours: 2 })
  }).then(r => r.json());
  const line = demand.lines[0];
  const detail = await fetch('/api/roads/break-demand/detail', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      kwhPerKm: 1.2,
      windowStartHours: 3.5,
      windowEndHours: 4.5,
      breakDurationHours: 0.75,
      shiftResetGapHours: 2,
      road: { lat1: line.lat1, lon1: line.lon1, lat2: line.lat2, lon2: line.lon2, radiusKm: line.selectionRadiusKm || 3 }
    })
  }).then(r => r.json());
  return {
    lines: demand.lines.length,
    firstPeak: line?.peakMw ?? 0,
    detailStatus: detail.status,
    quarterCells: detail.quarterProfile?.length ?? 0,
    vehicleRows: detail.vehiclesInWindow?.length ?? 0
  };
});
```

Expected: `lines > 0`, `detailStatus` is `ok`, `quarterCells > 0`, and `vehicleRows > 0` on production data.

- [ ] **Step 5: Publish live**

Run:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet publish src/LaadinfrastructuurPlanner -c Release -o /Users/johnnynijenhuis/route-analyse/app
```

- [ ] **Step 6: Restart live**

Run:

```bash
pids=$(lsof -tiTCP:5198 -sTCP:LISTEN || true)
if [ -n "$pids" ]; then kill $pids 2>/dev/null || true; sleep 2; fi
pids=$(lsof -tiTCP:5198 -sTCP:LISTEN || true)
if [ -n "$pids" ]; then kill -9 $pids 2>/dev/null || true; sleep 1; fi
: > /Users/johnnynijenhuis/route-analyse/app/live.log
cd /Users/johnnynijenhuis/route-analyse/app && ASPNETCORE_ENVIRONMENT=Production DOTNET_ROOT="$HOME/.dotnet" nohup "$HOME/.dotnet/dotnet" LaadinfrastructuurPlanner.dll --urls http://localhost:5198 > live.log 2>&1 &
```

- [ ] **Step 7: Verify live APIs**

Run:

```bash
curl -fsS --max-time 30 http://localhost:5198/api/metadata
curl -fsS --max-time 45 http://localhost:5198/api/roads/break-demand \
  -H 'Content-Type: application/json' \
  -d '{"kwhPerKm":1.2,"windowStartHours":3.5,"windowEndHours":4.5,"breakDurationHours":0.75,"shiftResetGapHours":2}' | jq '{status, lines:(.lines|length), diagnostics:.diagnostics}'
```

Expected: metadata returns `dataAvailable: true`; break-demand returns `status: ok`.

- [ ] **Step 8: Push**

Run:

```bash
git push origin HEAD:montevideo-v1
git push origin HEAD:main
```

Expected: both branches point at the implementation commit and GitHub CI starts.
