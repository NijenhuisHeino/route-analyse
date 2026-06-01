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
