using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using PanelFleet.Application;
using PanelFleet.Application.Operations;
using PanelFleet.Application.Overview;
using PanelFleet.Application.Resources;
using PanelFleet.Application.Settings;
using PanelFleet.Client;
using PanelFleet.Infrastructure;
using PanelFleet.Infrastructure.Persistence;
using PanelFleet.Web.Auth;
using PanelFleet.Web.BackgroundServices;
using PanelFleet.Web.Components;
using PanelFleet.Web.Endpoints;
using PanelFleet.Web.Json;
using PanelFleet.Web.Options;
using PanelFleet.Web.Terminal;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var dataDirectory = ResolveDataDirectory(configuration);
Directory.CreateDirectory(dataDirectory);
var keysDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keysDirectory);

var listenUrl = configuration["PanelFleet:Listen:Url"];
if (!string.IsNullOrWhiteSpace(listenUrl))
{
    builder.WebHost.UseUrls(listenUrl);
}

var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("PanelFleet")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

var forwardedHeadersEnabled = configuration.GetValue<bool>("PanelFleet:ForwardedHeaders:Enabled");
if (forwardedHeadersEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddPanelFleetApplication();
builder.Services.AddPanelFleetInfrastructure(dataDirectory);

var overviewOptions = configuration.GetSection("Overview").Get<OverviewOptions>() ?? new OverviewOptions();
overviewOptions.Validate();
builder.Services.AddSingleton(overviewOptions);
builder.Services.AddHostedService<OverviewRefreshHost>();

var resourceOptions = configuration.GetSection("Resources").Get<ResourceOptions>() ?? new ResourceOptions();
resourceOptions.Validate();
builder.Services.AddSingleton(resourceOptions);
builder.Services.AddHostedService<ResourceRefreshHost>();

var operationOptions = configuration.GetSection("Operations").Get<OperationOptions>() ?? new OperationOptions();
operationOptions.Validate();
builder.Services.AddSingleton(operationOptions);

var panelHttpOptions = configuration.GetSection("OnePanel").Get<PanelHttpOptions>() ?? new PanelHttpOptions();
builder.Services.AddSingleton(panelHttpOptions);
builder.Services.AddSingleton<IPanelClientFactory>(serviceProvider => new PanelClientFactory(
    panelHttpOptions,
    TimeProvider.System,
    serviceProvider.GetRequiredService<ILoggerFactory>()));

builder.Services.AddSingleton(configuration.GetSection("Health").Get<HealthOptions>() ?? new HealthOptions());
builder.Services.AddHostedService<NodeHealthMonitor>();

builder.Services.AddPanelFleetAuth(configuration);
builder.Services.AddTerminalModule(configuration);
builder.Services.AddSettingsModule();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.Converters.Add(new NodeIdJsonConverter());
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<PanelFleetDbContext>();
    var paths = scope.ServiceProvider.GetRequiredService<PanelFleetDataPaths>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PanelFleet.Database");
    await DatabaseInitializer.InitializeAsync(dbContext, paths.DatabasePath, logger).ConfigureAwait(false);
}

if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler("/error");

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapPanelFleetEndpoints();
app.MapTerminalEndpoints();

app.MapGet("/healthz", () => Results.Text("ok", "text/plain")).AllowAnonymous();
app.MapGet(
        "/readyz",
        async (PanelFleetDbContext dbContext, CancellationToken cancellationToken) =>
        {
            try
            {
                var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
                return canConnect
                    ? Results.Text("ok", "text/plain")
                    : Results.Text("degraded", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception)
            {
                return Results.Text("degraded", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .AllowAnonymous();

app.Run();

static string ResolveDataDirectory(IConfiguration configuration)
{
    var configured = configuration["PanelFleet:DataDirectory"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured);
    }

    var root = OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        : AppContext.BaseDirectory;

    return Path.Combine(root, "PanelFleet");
}

public partial class Program;
