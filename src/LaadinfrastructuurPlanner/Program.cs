using LaadinfrastructuurPlanner.Components;
using LaadinfrastructuurPlanner.Endpoints;
using LaadinfrastructuurPlanner.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(_ => RouteAnalysisOptionsFactory.FromContentRoot(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<DuckDbRouteStore>();
builder.Services.AddSingleton<RouteAnalysisService>();
builder.Services.AddHostedService<PlannerWarmupService>();

var app = builder.Build();
var allowedEmails = ParseAllowedEmails(builder.Configuration["ROUTE_ANALYSIS_ALLOWED_EMAILS"]);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use(async (context, next) =>
{
    if (RequiresAllowedEmail(context, allowedEmails))
    {
        var email = context.Request.Headers["Cf-Access-Authenticated-User-Email"].ToString();
        if (string.IsNullOrWhiteSpace(email))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Login vereist.");
            return;
        }

        if (!allowedEmails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Geen toegang voor dit e-mailadres.");
            return;
        }
    }

    await next(context);
});
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPlannerApi("/api");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static HashSet<string> ParseAllowedEmails(string? value)
{
    return (value ?? string.Empty)
        .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static bool RequiresAllowedEmail(HttpContext context, HashSet<string> allowedEmails)
{
    if (allowedEmails.Count == 0)
    {
        return false;
    }

    var host = context.Request.Host.Host;
    return !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        && !host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}

public partial class Program;
