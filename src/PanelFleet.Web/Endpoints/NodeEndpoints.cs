using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

public static class NodeEndpoints
{
    public static IEndpointRouteBuilder MapNodeEndpoints(this IEndpointRouteBuilder app)
    {
        var nodes = app.MapGroup("/api/nodes").RequireAuthorization(AuthSetup.AdminPolicy);
        var groups = app.MapGroup("/api/node-groups").RequireAuthorization(AuthSetup.AdminPolicy);

        nodes.MapGet("", async (INodeService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken).ConfigureAwait(false)));

        nodes.MapGet("/{id:guid}", async (Guid id, INodeService service, CancellationToken cancellationToken) =>
        {
            var list = await service.ListAsync(cancellationToken).ConfigureAwait(false);
            var node = list.FirstOrDefault(item => item.Id.Value == id);
            return node is null ? Results.NotFound(new { message = "节点不存在。" }) : Results.Ok(node);
        });

        nodes.MapPost("", async (
                CreateNodeRequest request,
                INodeService service,
                IActorProvider actors,
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
                    var summary = await service.CreateAsync(request, await actors.GetAsync(), cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/nodes/{summary.Id.Value:D}", summary);
                }).ConfigureAwait(false);
            });

        nodes.MapPut("/{id:guid}", async (
                Guid id,
                UpdateNodeRequest request,
                INodeService service,
                IActorProvider actors,
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
                    var summary = await service.UpdateAsync(new NodeId(id), request, await actors.GetAsync(), cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(summary);
                }).ConfigureAwait(false);
            });

        nodes.MapDelete("/{id:guid}", async (
                Guid id,
                INodeService service,
                IActorProvider actors,
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
                    await service.DeleteAsync(new NodeId(id), await actors.GetAsync(), cancellationToken)
                        .ConfigureAwait(false);
                    return Results.NoContent();
                }).ConfigureAwait(false);
            });

        nodes.MapPost("/{id:guid}/test", async (
                Guid id,
                INodeService service,
                IActorProvider actors,
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
                    var result = await service.TestSavedAsync(new NodeId(id), await actors.GetAsync(), cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(result);
                }).ConfigureAwait(false);
            });

        nodes.MapPost("/test", async (
                TestDraftRequest request,
                INodeService service,
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
                    var result = await service.TestDraftAsync(request, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(result);
                }).ConfigureAwait(false);
            });

        groups.MapGet("", async (INodeService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListGroupsAsync(cancellationToken).ConfigureAwait(false)));

        groups.MapPost("", async (
                GroupCreateRequest request,
                INodeService service,
                IActorProvider actors,
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
                    var group = await service.CreateGroupAsync(request, await actors.GetAsync(), cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/node-groups/{group.Id:D}", group);
                }).ConfigureAwait(false);
            });

        groups.MapPut("/{id:guid}", async (
                Guid id,
                GroupUpdateRequest request,
                INodeService service,
                IActorProvider actors,
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
                    var group = await service.UpdateGroupAsync(id, request, await actors.GetAsync(), cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(group);
                }).ConfigureAwait(false);
            });

        groups.MapDelete("/{id:guid}", async (
                Guid id,
                INodeService service,
                IActorProvider actors,
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
                    await service.DeleteGroupAsync(id, await actors.GetAsync(), cancellationToken)
                        .ConfigureAwait(false);
                    return Results.NoContent();
                }).ConfigureAwait(false);
            });

        app.MapGet("/api/audit", async (
                int? take,
                string? action,
                Guid? nodeId,
                IAuditStore store,
                CancellationToken cancellationToken) =>
            {
                var entries = await store.ListAsync(
                    take ?? 100,
                    action,
                    nodeId is { } id ? new NodeId(id) : null,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(entries);
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        app.MapGet("/api/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                return Results.Ok(new { token = tokens.RequestToken });
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        return app;
    }
}
