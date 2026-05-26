using LaadinfrastructuurPlanner.Components;
using LaadinfrastructuurPlanner.Endpoints;
using LaadinfrastructuurPlanner.Services;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.SignalR;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

var disableDetailedErrors = string.Equals(
    Environment.GetEnvironmentVariable("ROUTE_ANALYSIS_DETAILED_ERRORS"),
    "false",
    StringComparison.OrdinalIgnoreCase);
var detailedErrors = !disableDetailedErrors;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Surface real exception messages to the browser so the Blazor error UI is actionable.
        // Set ROUTE_ANALYSIS_DETAILED_ERRORS=false in production to revert to generic messages.
        options.DetailedErrors = detailedErrors;
    });
// The endpoint option above doesn't always propagate to CircuitOptions in production
// hosting setups; configure both explicitly so circuit-level exceptions also surface.
builder.Services.Configure<CircuitOptions>(options =>
{
    options.DetailedErrors = detailedErrors;
});
builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});
builder.Services.AddSingleton<RecentExceptionBuffer>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new RecentExceptionLoggerProvider(sp.GetRequiredService<RecentExceptionBuffer>()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(_ => RouteAnalysisOptionsFactory.FromContentRoot(builder.Environment.ContentRootPath, builder.Configuration));
builder.Services.AddSingleton<DuckDbRouteStore>();
builder.Services.AddSingleton<RouteAnalysisService>();
builder.Services.AddHttpClient(nameof(FleetDataService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LaadinfrastructuurPlanner/1.0 (info@nijenhuistrucksolutions.nl)");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl");
});
builder.Services.AddSingleton<FleetDataService>();
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
            await WriteAccessPageAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "Inloggen vereist",
                "Je sessie is verlopen of je bent nog niet aangemeld.");
            return;
        }

        if (!allowedEmails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            await WriteAccessPageAsync(
                context,
                StatusCodes.Status403Forbidden,
                "Geen toegang voor dit e-mailadres",
                $"Je bent aangemeld met {email.Trim()}. Log uit en probeer opnieuw met een toegestaan e-mailadres.");
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

static async Task WriteAccessPageAsync(HttpContext context, int statusCode, string title, string message)
{
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "text/html; charset=utf-8";

    var encodedTitle = WebUtility.HtmlEncode(title);
    var encodedMessage = WebUtility.HtmlEncode(message);
    await context.Response.WriteAsync(
        $$"""
        <!doctype html>
        <html lang="nl">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{encodedTitle}}</title>
            <style>
                :root {
                    color-scheme: light;
                    font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
                    background: #f5f7fb;
                    color: #21183f;
                }

                body {
                    margin: 0;
                    min-height: 100vh;
                    display: grid;
                    place-items: center;
                    padding: 24px;
                }

                main {
                    width: min(440px, 100%);
                    background: white;
                    border: 1px solid #dfe4ee;
                    border-radius: 8px;
                    box-shadow: 0 18px 45px rgba(20, 31, 56, 0.12);
                    padding: 28px;
                }

                h1 {
                    margin: 0 0 12px;
                    font-size: 24px;
                    line-height: 1.2;
                }

                p {
                    margin: 0 0 22px;
                    color: #5c6475;
                    line-height: 1.5;
                }

                a {
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    min-height: 42px;
                    padding: 0 16px;
                    border-radius: 7px;
                    background: #21183f;
                    color: white;
                    font-weight: 700;
                    text-decoration: none;
                }

                small {
                    display: block;
                    margin-top: 16px;
                    color: #778095;
                    line-height: 1.45;
                }
            </style>
        </head>
        <body>
            <main>
                <h1>{{encodedTitle}}</h1>
                <p>{{encodedMessage}}</p>
                <a href="/cdn-cgi/access/logout">Opnieuw inloggen</a>
                <small>Hiermee wordt je huidige Cloudflare-sessie afgesloten. Open daarna de app opnieuw en kies het juiste e-mailadres.</small>
            </main>
        </body>
        </html>
        """);
}

public partial class Program;
