using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Abstractions;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        var overview = app.MapGroup("/api/overview").RequireAuthorization(AuthSetup.AdminPolicy);

        overview.MapGet(
            "",
            async (IOverviewService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)));

        overview.MapPost(
            "/refresh",
            async (HttpContext context, IAntiforgery antiforgery, IOverviewRefreshQueue queue) =>
            {
                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                queue.RequestFull();
                return Results.Accepted(value: new { accepted = true });
            });

        overview.MapPost(
            "/nodes/{id:guid}/refresh",
            async (
                Guid id,
                IOverviewService service,
                HttpContext context,
                IAntiforgery antiforgery,
                CancellationToken cancellationToken) =>
            {
                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                return await EndpointHelpers.ExecuteAsync(async () =>
                    {
                        var snapshot = await service
                            .RefreshNodeAsync(new NodeId(id), force: true, cancellationToken)
                            .ConfigureAwait(false);
                        return Results.Ok(snapshot);
                    })
                    .ConfigureAwait(false);
            });

        return app;
    }
}
