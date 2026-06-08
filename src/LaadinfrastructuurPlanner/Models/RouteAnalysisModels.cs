namespace LaadinfrastructuurPlanner.Models;

public record AnalysisFilter
{
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public string[] VervoerderTypes { get; init; } = [];
    public string[] Vervoerders { get; init; } = [];
    public string[] Wagencodes { get; init; } = [];
    public double MinDwellMin { get; init; }
    public int RoadThreshold { get; init; } = 1;
    public int RoadTopPercent { get; init; } = 1;
    public int MarkerTopN { get; init; } = 250;
    public string ZeZoneMode { get; init; } = "all";
}

public sealed record SimulationRequest : AnalysisFilter
{
    public double KwhPerKm { get; init; } = 1.2;
    public double CapacityKwh { get; init; } = 590;
    public double StartSocPct { get; init; } = 100;
    public double ThresholdPct { get; init; } = 15;
    public double MaxChargeKw { get; init; } = 350;
}

public sealed record ChargerFilter
{
    public double MinPowerKw { get; init; } = 350;
    public int MinConnectors { get; init; } = 1;
    public bool OnlyDedicated { get; init; }
    public string[] Access { get; init; } = ["Publiek", "Semi-publiek"];
}

public sealed record CacheFileStatus(
    string Name,
    bool Exists,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc);

public sealed record MetadataResponse(
    bool DataAvailable,
    string? DataSource,
    DateOnly? MinDate,
    DateOnly? MaxDate,
    long StopCount,
    long TripCount,
    long WagenCount,
    string[] VervoerderTypes,
    string[] Vervoerders,
    string[] Wagencodes,
    string[] ZeZones,
    CacheFileStatus[] CacheFiles);

public sealed record DatasetUploadResult(
    bool Success,
    string Message,
    string? DataSource,
    long FileCount,
    long SizeBytes);

public sealed record SummaryResponse(
    long Stops,
    long Trips,
    long Wagens,
    double TotalKm,
    double AvgTripKm,
    double TotalDwellHours,
    double EigenKmPct,
    double CharterKmPct,
    bool FromCache);

public sealed record HeatPoint(double Lat, double Lon, double Weight);

public sealed record StopMarker(
    double Lat,
    double Lon,
    string Name,
    string Address,
    long UniqueWagens,
    long Stops,
    long Trips,
    double AvgDwellMin);

public sealed record StopMapResponse(
    HeatPoint[] HeatPoints,
    StopMarker[] Markers,
    bool Aggregated,
    long SourceStops,
    bool FromCache);

public sealed record RoadPoint(double Lat, double Lon);

public sealed record RoadLine(
    double Lat1,
    double Lon1,
    double Lat2,
    double Lon2,
    int UniqueWagens,
    int Passes,
    string SegmentId = "",
    string RoadName = "",
    string Direction = "",
    double Bearing = 0,
    double LengthKm = 0,
    int RawSegments = 1,
    double SelectionRadiusKm = 3,
    RoadPoint[]? Coordinates = null);

public sealed record RoadMapResponse(
    string Status,
    string? Variant,
    string? Message,
    RoadLine[] Lines,
    HeatPoint[] HeatPoints,
    bool FromCache);

public sealed record ChargerMarker(
    long Id,
    double Lat,
    double Lon,
    string Name,
    string Operator,
    string Address,
    string Town,
    string Postcode,
    double MaxPowerKw,
    long Connectors,
    string Access,
    string TwentyfourSeven,
    string Dedicated,
    string ConnectorType);

public sealed record ChargerMapResponse(
    string Status,
    ChargerMarker[] Markers,
    bool FromCache);

public sealed record ChargingScenario
{
    public double KwhPerKm { get; init; } = 1.2;
    public double CapacityKwh { get; init; } = 590;
    public double MinSocPct { get; init; } = 15;
    public double TargetSocPct { get; init; } = 80;
    public double KwPerPlug { get; init; } = 350;
    public int Plugs { get; init; } = 4;
    public double SiteLimitMw { get; init; } = 1.4;
}

public sealed record OvernightLocationsRequest : AnalysisFilter
{
    public int MinVehicles { get; init; } = 5;
    public ChargingScenario Scenario { get; init; } = new();
}

public sealed record OvernightLocationMarker(
    string DepotId,
    double Lat,
    double Lon,
    string Address,
    long UniqueVehicles,
    long Events,
    double MedianGapHours,
    double P95DayKm,
    double TotalMwh,
    double ShortageMwh,
    double NearestPublicChargerKm,
    string Recommendation);

public sealed record OvernightLocationsResponse(
    string Status,
    string? Message,
    OvernightLocationMarker[] Locations,
    bool FromCache);

public sealed record OvernightLocationDetailRequest : AnalysisFilter
{
    public string DepotId { get; init; } = "";
    public ChargingScenario Scenario { get; init; } = new();
}

public sealed record StopLocationDetailRequest : AnalysisFilter
{
    public double Lat { get; init; }
    public double Lon { get; init; }
    public string? Label { get; init; }
    public double RadiusKm { get; init; } = 0.5;
    public ChargingScenario Scenario { get; init; } = new();
}

public sealed record RoadSelection(
    double Lat1,
    double Lon1,
    double Lat2,
    double Lon2,
    double RadiusKm = 3);

public sealed record RoadSelectionRequest : AnalysisFilter
{
    public RoadSelection Road { get; init; } = new(0, 0, 0, 0);
    public ChargingScenario Scenario { get; init; } = new();
}

public record RoadBreakDemandRequest : AnalysisFilter
{
    public double KwhPerKm { get; init; } = 1.2;
    public double CapacityKwh { get; init; } = 590;
    public double TargetSocPct { get; init; } = 100;
    public double WindowStartHours { get; init; } = 3.5;
    public double WindowEndHours { get; init; } = 4.5;
    public double BreakDurationHours { get; init; } = 0.75;
    public double ShiftResetGapHours { get; init; } = 2.0;
    public double ResetLocationRadiusKm { get; init; } = 0.75;
    public double MaxAverageSpeedKmh { get; init; } = 95;
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
    HourlyDemandCell[] HourlyProfile,
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

public sealed record ChargingScenarioRequest : AnalysisFilter
{
    public string SelectionType { get; init; } = "depot";
    public string? DepotId { get; init; }
    public RoadSelection? Road { get; init; }
    public ChargingScenario Scenario { get; init; } = new();
}

public sealed record DistanceBucket(double FromKm, double ToKm, long Trips);

public sealed record DistanceDistribution(
    long Trips,
    double AvgKm,
    double StdDevKm,
    double P50Km,
    double P75Km,
    double P90Km,
    double P95Km,
    DistanceBucket[] Buckets);

public sealed record SelectionSummary(
    long Trips,
    long UniqueVehicles,
    double TotalKm,
    double TotalMwh,
    double AvgGapHours,
    string Recommendation);

public sealed record SelectionVehicleRow(
    string Wagencode,
    string Kentekens,
    long Days,
    long Passages,
    double TotalKm,
    double AvgDayKm,
    double P95Km,
    double AvgKwhPerDay,
    double TotalMwh,
    double AvgStandingHours,
    double RequiredKw);

public sealed record ChargingWindow(
    string TimeSlot,
    long Events,
    double DemandKwh,
    double DeliverableKwh,
    double ShortageKwh,
    double RequiredMw);

public sealed record HourlyDemandCell(
    int Hour,
    string Label,
    long Vehicles,
    long Events,
    double DemandKwh,
    double RequiredKw,
    double RequiredMw);

public sealed record WeeklyDemandCell(
    int DayIndex,
    string DayLabel,
    int Hour,
    string Label,
    long Vehicles,
    long Events,
    double DemandKwh,
    double RequiredKw,
    double RequiredMw,
    string[] Kentekens,
    string[] Wagencodes,
    WeeklyDemandVehicle[] VehicleDemands);

public sealed record WeeklyDemandVehicle(
    string Wagencode,
    string Kenteken,
    double DemandKwh,
    double RequiredKw,
    double StandingHours,
    string Window);

public sealed record ChargingProfile(
    long Events,
    double TotalMwh,
    double ShortageMwh,
    double PeakMw,
    int RequiredPlugsAtPeak,
    ChargingWindow[] BusyWindows,
    HourlyDemandCell[] HourlyProfile,
    WeeklyDemandCell[] WeeklyProfile,
    string Recommendation);

public record PowerProfileRequest : AnalysisFilter
{
    public string[] VehicleClasses { get; init; } = [];
    public int TopLocations { get; init; } = 5;
    public int[] ScenarioYears { get; init; } = [];
    public string ScenarioMode { get; init; } = "linear";
    public double CapacityKwh { get; init; } = 590;
    public double MaxVehicleKw { get; init; } = 400;
}

public sealed record PowerLocationProfileRequest : PowerProfileRequest
{
    public string LocationId { get; init; } = "";
}

public sealed record PowerHourlyCell(
    int Hour,
    string Label,
    long Vehicles,
    long Events,
    double RequiredKw,
    double RequiredMw,
    DateOnly? Date,
    PowerHourlyVehicle[] VehicleDemands);

public sealed record PowerHourlyVehicle(
    string Wagencode,
    string Kenteken,
    string VehicleClass,
    double DemandKwh,
    double RequiredKw,
    double StandingHours,
    string Window);

public sealed record PowerHeatmapCell(
    string LocationId,
    string LocationName,
    int Hour,
    long Vehicles,
    double RequiredKw,
    double RequiredMw);

public sealed record PowerDailyMetric(
    DateOnly Date,
    long UniqueVehicles,
    long OwnVehicles,
    long CharterVehicles,
    long Trips,
    long Events,
    double AvgDwellMin,
    double PeakKw);

public sealed record PowerLocationProfile(
    string LocationId,
    string Name,
    string Address,
    double Lat,
    double Lon,
    long UniqueVehicles,
    long UniqueOwnVehicles,
    long UniqueCharterVehicles,
    long Trips,
    long Events,
    double AvgDwellMin,
    double PeakKw,
    PowerHourlyCell[] HourlyProfile);

public sealed record PowerScenarioProfile(
    int Year,
    string Mode,
    double ScaleFactor,
    PowerHourlyCell[] HourlyProfile);

public sealed record PowerProfileResponse(
    string Status,
    string? Message,
    PowerLocationProfile[] Locations,
    PowerHeatmapCell[] Heatmap,
    PowerScenarioProfile[] Scenarios,
    bool FromCache);

public sealed record PowerLocationProfileResponse(
    string Status,
    string? Message,
    PowerLocationProfile? Profile,
    PowerDailyMetric[] DailyMetrics,
    PowerScenarioProfile[] Scenarios,
    bool FromCache);

public sealed record VehicleClassCount(string VehicleClass, long Vehicles, long Events);

public sealed record PowerDiagnosticsResponse(
    string Status,
    long TotalActions,
    long ChargeWindowActions,
    long MissingLocationActions,
    long UnknownVehicleClassActions,
    long RoutesWithoutWaitWindow,
    long FleetVehicles,
    long FleetVehiclesMatchedInRoutes,
    VehicleClassCount[] VehicleClassCounts,
    string[] Assumptions,
    bool FromCache);

public sealed record PowerReportExportResponse(
    string Status,
    string Message,
    string OutputDirectory,
    string[] Files);

public sealed record SelectionDetailResponse(
    string Status,
    string SelectionType,
    string? SelectionId,
    string Title,
    string? Message,
    double Lat,
    double Lon,
    SelectionSummary Summary,
    DistanceDistribution Distribution,
    DistanceDistribution DailyDistanceDistribution,
    HeatPoint[] HeatPoints,
    SelectionVehicleRow[] Vehicles,
    ChargingProfile Charging,
    bool FromCache);

public sealed record DashboardStop(
    double Lat,
    double Lon,
    string Name,
    string Address,
    long Stops,
    long Wagens,
    long Trips,
    double TotalDwellHours,
    double AvgDwellMin);

public sealed record DashboardCorridor(
    int Rank,
    double Lat,
    double Lon,
    int MedianPasses,
    int MaxPasses,
    int MaxWagens,
    double SpreadKm,
    int RoadSegments);

public sealed record ZeZoneSummary(
    string Zone,
    string StartDate,
    long Stops,
    long Trips,
    long Wagens);

public sealed record DashboardResponse(
    DashboardStop[] TopStops,
    DashboardCorridor[] Corridors,
    RoadLine[] TopRoadSegments,
    ZeZoneSummary[] ZeZones,
    string RoadStatus,
    bool FromCache);

public sealed record SimulationHotspot(
    double Lat,
    double Lon,
    string Address,
    long Events,
    long UniqueWagens,
    double TotalKwh,
    double AvgChargeMin);

public sealed record TripSimulationSummary(
    string TripId,
    long Stops,
    double TripKm,
    double EndSocPct,
    long ChargeEvents,
    double ChargeKwh,
    double ChargeMin);

public sealed record SimulationResponse(
    long Trips,
    long TripsWithChargeEvent,
    long ChargeEvents,
    double TotalKwh,
    double TotalChargeHours,
    SimulationHotspot[] Hotspots,
    TripSimulationSummary[] TripsTop,
    bool FromCache);

public sealed record FleetVehicle(
    string Vlootnummer,
    string Kenteken,
    string KentekenNorm,
    string Regio,
    string Opstapplaats,
    string TypeLocatie,
    string Merk,
    string SoortVoertuig,
    string SoortBrandstof,
    long TripsInData,
    double KmInData);

public sealed record FleetDepot(
    string DepotId,
    string Name,
    string Regio,
    string TypeLocatie,
    double Lat,
    double Lon,
    string GeocodeQuery,
    string GeocodeSource,
    long Vehicles,
    long MatchedInTrips,
    FleetVehicle[] VehicleList);

public sealed record FleetDepotsResponse(
    string Status,
    string? Message,
    string SourceLabel,
    string Disclaimer,
    long TotalVehicles,
    FleetDepot[] Depots);
