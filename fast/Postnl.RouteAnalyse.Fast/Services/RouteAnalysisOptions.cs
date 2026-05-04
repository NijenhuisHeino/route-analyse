namespace Postnl.RouteAnalyse.Fast.Services;

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
        var repoRoot = Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
        var cacheDir = Path.Combine(repoRoot, ".cache");
        var fastCacheDir = Path.Combine(cacheDir, "fast");
        var defaultOriginalCsvDir = Path.Combine(DefaultDataRoot, "Rittendata per maand ", "Rittendata");
        var defaultExternalCacheDir = Path.Combine(DefaultDataRoot, "Route analyse tool", "cache-backup");
        var originalCsvDir = Environment.GetEnvironmentVariable("POSTNL_ORIGINAL_CSV_DIR");
        var externalCacheDir = Environment.GetEnvironmentVariable("POSTNL_EXTERNAL_CACHE_DIR");

        return new RouteAnalysisOptions
        {
            RepoRoot = repoRoot,
            CacheDir = cacheDir,
            DuckDbPath = Path.Combine(fastCacheDir, "route-analysis.duckdb"),
            ManifestPath = Path.Combine(fastCacheDir, "manifest.json"),
            OriginalCsvDir = FirstExistingDirectory(originalCsvDir, defaultOriginalCsvDir),
            ExternalCacheDir = FirstExistingDirectory(externalCacheDir, defaultExternalCacheDir),
        };
    }

    private static string? FirstExistingDirectory(params string?[] candidates)
    {
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }
}
