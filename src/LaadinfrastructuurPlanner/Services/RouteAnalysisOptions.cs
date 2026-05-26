namespace LaadinfrastructuurPlanner.Services;

public sealed class RouteAnalysisOptions
{
    public required string RepoRoot { get; init; }
    public required string CacheDir { get; init; }
    public required string UploadedDatasetDir { get; init; }
    public required string DuckDbPath { get; init; }
    public required string ManifestPath { get; init; }
    public string? OriginalCsvDir { get; init; }
    public string? ExternalCacheDir { get; init; }
    public string? ZeZonesSourcePath { get; init; }
    public string? FleetExcelPath { get; init; }
    public VehiclePowerAssumption[] VehiclePowerAssumptions { get; init; } = RouteAnalysisDefaults.VehiclePowerAssumptions;
    public ScenarioInflowAssumption[] ScenarioInflows { get; init; } = RouteAnalysisDefaults.ScenarioInflows;
    public Models.VehicleEnergyAssumption[] VehicleEnergyAssumptions { get; init; } = RouteAnalysisDefaults.VehicleEnergyAssumptions;
    public string FleetRolloutMode { get; init; } = "linear";
    public double FleetRolloutK { get; init; } = 1.1;
    public int FleetRolloutT0Year { get; init; } = 2029;
    public string? GeocodingOverridePath { get; init; }
    public string FocusLocationAlias { get; init; } = "Nieuwegein (Groteweerd 80)";
}

public static class RouteAnalysisOptionsFactory
{
    public static RouteAnalysisOptions FromContentRoot(string contentRoot, IConfiguration? configuration = null)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("ROUTE_ANALYSIS_REPO_ROOT");
        var repoRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
        var cacheDir = Path.Combine(repoRoot, ".cache");
        var plannerCacheDir = Path.Combine(cacheDir, "planner");
        var originalCsvDir = Environment.GetEnvironmentVariable("ROUTE_ANALYSIS_ORIGINAL_CSV_DIR");
        var externalCacheDir = Environment.GetEnvironmentVariable("ROUTE_ANALYSIS_EXTERNAL_CACHE_DIR");
        var zeZonesSourcePath = Environment.GetEnvironmentVariable("ROUTE_ANALYSIS_ZE_ZONES_PATH");
        var fleetExcelEnv = Environment.GetEnvironmentVariable("ROUTE_ANALYSIS_FLEET_EXCEL_PATH");
        var driveDataDir = "/Users/johnnynijenhuis/Library/CloudStorage/GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/Data analyse ritten/Route analyse tool/data";

        var powerAssumptions = configuration?.GetSection("RouteAnalysis:VehiclePowerAssumptions").Get<VehiclePowerAssumption[]>()
            ?? RouteAnalysisDefaults.VehiclePowerAssumptions;
        var scenarioInflows = configuration?.GetSection("RouteAnalysis:ScenarioInflows").Get<ScenarioInflowAssumption[]>()
            ?? RouteAnalysisDefaults.ScenarioInflows;
        var energyAssumptions = configuration?.GetSection("RouteAnalysis:VehicleEnergyAssumptions").Get<Models.VehicleEnergyAssumption[]>()
            ?? RouteAnalysisDefaults.VehicleEnergyAssumptions;
        var focusLocationAlias = configuration?["RouteAnalysis:FocusLocationAlias"];
        var fleetRolloutMode = configuration?["RouteAnalysis:FleetRolloutMode"];
        var fleetRolloutK = configuration?.GetValue<double?>("RouteAnalysis:FleetRolloutK");
        var fleetRolloutT0 = configuration?.GetValue<int?>("RouteAnalysis:FleetRolloutT0Year");
        var geocodingOverridePath = Environment.GetEnvironmentVariable("ROUTE_ANALYSIS_GEOCODING_OVERRIDE")
            ?? configuration?["RouteAnalysis:GeocodingOverridePath"];

        return new RouteAnalysisOptions
        {
            RepoRoot = repoRoot,
            CacheDir = cacheDir,
            UploadedDatasetDir = Path.Combine(cacheDir, "uploaded-dataset", "active"),
            DuckDbPath = Path.Combine(plannerCacheDir, "route-analysis.duckdb"),
            ManifestPath = Path.Combine(plannerCacheDir, "manifest.json"),
            OriginalCsvDir = FirstExistingDirectory(originalCsvDir),
            ExternalCacheDir = FirstExistingDirectory(externalCacheDir),
            ZeZonesSourcePath = FirstExistingFile(zeZonesSourcePath),
            FleetExcelPath = FirstExistingFile(
                fleetExcelEnv,
                Path.Combine(driveDataDir, "ev_wagenpark_standplaatsen.xlsx"),
                Path.Combine(cacheDir, "ev_wagenpark_standplaatsen.xlsx")),
            VehiclePowerAssumptions = powerAssumptions.Length == 0 ? RouteAnalysisDefaults.VehiclePowerAssumptions : powerAssumptions,
            ScenarioInflows = scenarioInflows.Length == 0 ? RouteAnalysisDefaults.ScenarioInflows : scenarioInflows,
            VehicleEnergyAssumptions = energyAssumptions.Length == 0 ? RouteAnalysisDefaults.VehicleEnergyAssumptions : energyAssumptions,
            FleetRolloutMode = string.IsNullOrWhiteSpace(fleetRolloutMode) ? "linear" : fleetRolloutMode,
            FleetRolloutK = fleetRolloutK ?? 1.1,
            FleetRolloutT0Year = fleetRolloutT0 ?? 2029,
            GeocodingOverridePath = FirstExistingFile(geocodingOverridePath, Path.Combine(repoRoot, "data", "fleet_geocoded.csv")),
            FocusLocationAlias = string.IsNullOrWhiteSpace(focusLocationAlias) ? "Nieuwegein (Groteweerd 80)" : focusLocationAlias,
        };
    }

    private static string? FirstExistingDirectory(params string?[] candidates)
    {
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    private static string? FirstExistingFile(params string?[] candidates)
    {
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}

public sealed record VehiclePowerAssumption(string Name, string[] MatchTerms, double PowerKw);

public sealed record ScenarioInflowAssumption(int Year, int TractorCount, int BoxTruckCount);

public static class RouteAnalysisDefaults
{
    public static VehiclePowerAssumption[] VehiclePowerAssumptions { get; } =
    [
        new("trekker", ["trekker", "tractor", "oplegger"], 350),
        new("bakwagen", ["bakwagen", "box", "rigid"], 150),
    ];

    public static ScenarioInflowAssumption[] ScenarioInflows { get; } =
    [
        new(2026, 3, 1),
        new(2027, 10, 16),
        new(2028, 21, 25),
        new(2029, 38, 25),
        new(2030, 56, 25),
        new(2031, 75, 25),
    ];

    public static Models.VehicleEnergyAssumption[] VehicleEnergyAssumptions { get; } =
    [
        new("trekker", "winter", 1.60),
        new("trekker", "summer", 1.30),
        new("bakwagen", "winter", 1.00),
        new("bakwagen", "summer", 0.85),
        new("unknown", "winter", 1.30),
        new("unknown", "summer", 1.10),
    ];
}
