using Postnl.LaadinfrastructuurPlanner.Components;
using Postnl.LaadinfrastructuurPlanner.Endpoints;
using Postnl.LaadinfrastructuurPlanner.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(_ => RouteAnalysisOptionsFactory.FromContentRoot(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<DuckDbRouteStore>();
builder.Services.AddSingleton<RouteAnalysisService>();
builder.Services.AddHostedService<PlannerWarmupService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPlannerApi("/api");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
