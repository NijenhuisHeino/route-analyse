namespace Postnl.RouteAnalyse.Fast.Services;

public sealed class FastCacheWarmupService : IHostedService
{
    private readonly DuckDbRouteStore _store;
    private readonly ILogger<FastCacheWarmupService> _logger;

    public FastCacheWarmupService(DuckDbRouteStore store, ILogger<FastCacheWarmupService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.EnsureReadyAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fast cache warmup failed; API will report cache state.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

