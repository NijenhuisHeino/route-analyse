using LaadinfrastructuurPlanner.Models;
using LaadinfrastructuurPlanner.Services;
using Microsoft.AspNetCore.Mvc;

namespace LaadinfrastructuurPlanner.Endpoints;

public static class PlannerApiEndpoints
{
    public static IEndpointRouteBuilder MapPlannerApi(this IEndpointRouteBuilder endpoints, string prefix)
    {
        var api = endpoints.MapGroup(prefix);

        api.MapGet("/metadata", (RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetMetadataAsync(cancellationToken));

        api.MapPost("/summary", ([FromBody] AnalysisFilter filter, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetSummaryAsync(filter, cancellationToken));

        api.MapPost("/map/stops", ([FromBody] AnalysisFilter filter, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetStopMapAsync(filter, cancellationToken));

        api.MapPost("/map/roads", ([FromBody] AnalysisFilter filter, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetRoadMapAsync(filter, cancellationToken));

        api.MapPost("/map/chargers", ([FromBody] ChargerFilter filter, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetChargersAsync(filter, cancellationToken));

        api.MapPost("/overnight/locations", ([FromBody] OvernightLocationsRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetOvernightLocationsAsync(request, cancellationToken));

        api.MapPost("/overnight/location", ([FromBody] OvernightLocationDetailRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetOvernightLocationDetailAsync(request, cancellationToken));

        api.MapPost("/stops/location", ([FromBody] StopLocationDetailRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetStopLocationDetailAsync(request, cancellationToken));

        api.MapPost("/roads/selection", ([FromBody] RoadSelectionRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetRoadSelectionAsync(request, cancellationToken));

        api.MapPost("/charging/scenario", ([FromBody] ChargingScenarioRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetChargingScenarioAsync(request, cancellationToken));

        api.MapPost("/power/profiles", ([FromBody] PowerProfileRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetPowerProfilesAsync(request, cancellationToken));

        api.MapPost("/power/location", ([FromBody] PowerLocationProfileRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetPowerLocationProfileAsync(request, cancellationToken));

        api.MapPost("/power/diagnostics", ([FromBody] AnalysisFilter filter, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetPowerDiagnosticsAsync(filter, cancellationToken));

        api.MapPost("/power/export/nieuwegein", (RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.ExportNieuwegeinPowerReportAsync(cancellationToken));

        api.MapGet("/data-quality", (RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetDataQualityReportAsync(cancellationToken));

        api.MapPost("/power/sensitivity", ([FromBody] PowerLocationProfileRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetSensitivityAsync(request, cancellationToken));

        api.MapPost("/dashboard", ([FromBody] AnalysisFilter filter, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetDashboardAsync(filter, cancellationToken));

        api.MapPost("/simulation", ([FromBody] SimulationRequest request, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetSimulationAsync(request, cancellationToken));

        api.MapGet("/fleet/standplaatsen", (FleetDataService fleet, CancellationToken cancellationToken) =>
            fleet.GetDepotsAsync(cancellationToken));

        api.MapGet("/fleet/match", (RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetFleetMatchReportAsync(cancellationToken));

        api.MapPost("/corridors/hotspots", ([FromBody] AnalysisFilter filter, RouteAnalysisService service, CancellationToken cancellationToken) =>
            service.GetCorridorHotspotsAsync(filter, null, 25, cancellationToken));

        return endpoints;
    }
}
