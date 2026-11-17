using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Configuration;
using PanelFleet.Application.Settings;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>PanelFleet 自身设置与 1Panel 面板设置的只读/写入口。</summary>
public static class SettingsEndpoints
{
    /// <summary>注册 <c>/api/settings</c> 端点组（仅 Admin）。</summary>
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var settings = app.MapGroup("/api/settings").RequireAuthorization(AuthSetup.AdminPolicy);

        settings.MapGet("/panelfleet", async (
            PanelFleetSettingsService service,
            INodeStore nodes,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var view = service.GetView();
            var nodeRecords = await nodes.ListAsync(cancellationToken);
            var environment = BuildEnvironment(configuration, nodeRecords);

            return Results.Ok(new
            {
                revision = view.Revision,
                updatedAt = view.UpdatedAt,
                values = view.Values,
                defaults = view.Defaults,
                sources = view.Sources,
                fields = view.Fields,
                environment
            });
        });

        settings.MapPut("/panelfleet", async (
            PanelFleetSettingsUpdate update,
            HttpContext context,
            IAntiforgery antiforgery,
            IActorProvider actors,
            PanelFleetSettingsService service,
            CancellationToken cancellationToken) =>
        {
            if (!await Csrf.ValidateAsync(context, antiforgery))
            {
                return Csrf.Reject();
            }

            var actor = await actors.GetAsync();
            try
            {
                var result = await service.UpdateAsync(update, actor, cancellationToken);
                var view = result.View;
                return Results.Ok(new
                {
                    revision = view.Revision,
                    updatedAt = view.UpdatedAt,
                    values = view.Values,
                    defaults = view.Defaults,
                    sources = view.Sources,
                    fields = view.Fields,
                    changedKeys = result.ChangedKeys
                });
            }
            catch (PanelFleetSettingsValidationException exception)
            {
                return Results.ValidationProblem(exception.Errors);
            }
        });

        settings.MapGet("/panel/{nodeId:guid}", async (
            Guid nodeId,
            PanelSettingsService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetAsync(nodeId, cancellationToken);
            return result.Found && result.View is not null
                ? Results.Ok(result.View)
                : Results.NotFound(new { message = $"节点 {nodeId} 不存在。" });
        });
    }

    private static PanelFleetEnvironmentInfo BuildEnvironment(
        IConfiguration configuration,
        IReadOnlyList<PanelFleet.Application.Models.NodeRecord> nodes)
    {
        var dataDirectory = configuration["PanelFleet:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            var root = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                : AppContext.BaseDirectory;
            dataDirectory = Path.Combine(root, "PanelFleet");
        }

        var authority = configuration["Oidc:Authority"];
        var clientId = configuration["Oidc:ClientId"];
        var version = typeof(SettingsEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SettingsEndpoints).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        return new PanelFleetEnvironmentInfo(
            DataDirectory: Path.GetFullPath(dataDirectory),
            ListenUrl: configuration["PanelFleet:Listen:Url"] ?? string.Empty,
            OidcAuthority: authority,
            OidcClientId: clientId,
            OidcConfigured: !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(clientId),
            NodeCount: nodes.Count,
            NodeEnabledCount: nodes.Count(node => node.IsEnabled),
            Version: version,
            Theme: configuration["PanelFleet:Theme"] ?? "system");
    }
}

/// <summary>只读的运行环境信息（设置页展示）。</summary>
public sealed record PanelFleetEnvironmentInfo(
    string DataDirectory,
    string ListenUrl,
    string? OidcAuthority,
    string? OidcClientId,
    bool OidcConfigured,
    int NodeCount,
    int NodeEnabledCount,
    string Version,
    string Theme);
