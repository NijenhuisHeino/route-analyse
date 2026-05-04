namespace Postnl.LaadinfrastructuurPlanner.Services;

public sealed class PlannerWarmupService : IHostedService
{
    private readonly DuckDbRouteStore _store;
    private readonly ILogger<PlannerWarmupService> _logger;

    public PlannerWarmupService(DuckDbRouteStore store, ILogger<PlannerWarmupService> logger)
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
            _logger.LogWarning(ex, "Planner data warmup failed; API will report data state.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
