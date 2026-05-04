namespace Postnl.LaadinfrastructuurPlanner.Services;

public sealed class RouteAnalysisOptions
{
    public required string RepoRoot { get; init; }
    public required string CacheDir { get; init; }
    public required string DuckDbPath { get; init; }
    public required string ManifestPath { get; init; }
    public string? OriginalCsvDir { get; init; }
    public string? ExternalCacheDir { get; init; }
}

public static class RouteAnalysisOptionsFactory
{
    private const string DefaultDataRoot =
        "/Users/johnnynijenhuis/Library/CloudStorage/GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/Data analyse ritten";

    public static RouteAnalysisOptions FromContentRoot(string contentRoot)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("POSTNL_REPO_ROOT");
        var repoRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
        var cacheDir = Path.Combine(repoRoot, ".cache");
        var plannerCacheDir = Path.Combine(cacheDir, "planner");
        var useDefaultDataRoot = !string.Equals(
            Environment.GetEnvironmentVariable("POSTNL_USE_DEFAULT_DATA_ROOT"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        var defaultOriginalCsvDir = useDefaultDataRoot ? Path.Combine(DefaultDataRoot, "Rittendata per maand ", "Rittendata") : null;
        var defaultExternalCacheDir = useDefaultDataRoot ? Path.Combine(DefaultDataRoot, "Route analyse tool", "cache-backup") : null;
        var originalCsvDir = Environment.GetEnvironmentVariable("POSTNL_ORIGINAL_CSV_DIR");
        var externalCacheDir = Environment.GetEnvironmentVariable("POSTNL_EXTERNAL_CACHE_DIR");

        return new RouteAnalysisOptions
        {
            RepoRoot = repoRoot,
            CacheDir = cacheDir,
            DuckDbPath = Path.Combine(plannerCacheDir, "route-analysis.duckdb"),
            ManifestPath = Path.Combine(plannerCacheDir, "manifest.json"),
            OriginalCsvDir = FirstExistingDirectory(originalCsvDir, defaultOriginalCsvDir),
            ExternalCacheDir = FirstExistingDirectory(externalCacheDir, defaultExternalCacheDir),
        };
    }

    private static string? FirstExistingDirectory(params string?[] candidates)
    {
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }
}
