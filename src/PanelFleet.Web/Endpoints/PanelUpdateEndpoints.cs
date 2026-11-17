using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.PanelUpdates;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>面板更新只读端点：版本检查（在线/当前版本/可用版本/更新说明/预检），不触发升级。</summary>
public static class PanelUpdateEndpoints
{
    public static IEndpointRouteBuilder MapPanelUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var updates = app.MapGroup("/api/panel-update").RequireAuthorization(AuthSetup.AdminPolicy);

        updates.MapGet(
            "/versions",
            async (Guid[]? nodeIds, IPanelUpdateService service, CancellationToken cancellationToken) =>
            {
                var filter = nodeIds is { Length: > 0 }
                    ? nodeIds.Distinct().Select(id => new NodeId(id)).ToArray()
                    : null;

                return Results.Ok(await service
                    .CheckVersionsAsync(filter, cancellationToken)
                    .ConfigureAwait(false));
            });

        return app;
    }
}
