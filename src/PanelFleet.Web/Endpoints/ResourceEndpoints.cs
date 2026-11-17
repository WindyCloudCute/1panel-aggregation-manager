using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

public static class ResourceEndpoints
{
    public static IEndpointRouteBuilder MapResourceEndpoints(this IEndpointRouteBuilder app)
    {
        var resources = app.MapGroup("/api/resources").RequireAuthorization(AuthSetup.AdminPolicy);

        resources.MapGet(
            "/{kind}",
            async (
                string kind,
                string? filter,
                string? sort,
                bool? descending,
                int? page,
                int? pageSize,
                Guid? nodeId,
                IResourceQueryService service,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseKind(kind, out var resourceKind))
                {
                    return Results.BadRequest(new { message = "未知资源类型。" });
                }

                var query = new ResourceQuery(
                    filter,
                    sort,
                    descending ?? false,
                    page ?? 1,
                    pageSize ?? 50,
                    nodeId is { } id ? new NodeId(id) : null);

                return Results.Ok(await service
                    .QueryAsync(resourceKind, query, ResourceQueryMode.EnsureFresh, cancellationToken)
                    .ConfigureAwait(false));
            });

        resources.MapPost(
            "/{kind}/refresh",
            async (
                string kind,
                string? filter,
                string? sort,
                bool? descending,
                int? page,
                int? pageSize,
                Guid? nodeId,
                IResourceQueryService service,
                HttpContext context,
                IAntiforgery antiforgery,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseKind(kind, out var resourceKind))
                {
                    return Results.BadRequest(new { message = "未知资源类型。" });
                }

                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                var query = new ResourceQuery(
                    filter,
                    sort,
                    descending ?? false,
                    page ?? 1,
                    pageSize ?? 50,
                    nodeId is { } id ? new NodeId(id) : null);

                return Results.Ok(await service.RefreshAsync(resourceKind, query, cancellationToken)
                    .ConfigureAwait(false));
            });

        resources.MapPost(
            "/{kind}/nodes/{nodeId:guid}/refresh",
            async (
                string kind,
                Guid nodeId,
                string? filter,
                string? sort,
                bool? descending,
                int? page,
                int? pageSize,
                IResourceQueryService service,
                HttpContext context,
                IAntiforgery antiforgery,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseKind(kind, out var resourceKind))
                {
                    return Results.BadRequest(new { message = "未知资源类型。" });
                }

                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                var query = new ResourceQuery(
                    filter,
                    sort,
                    descending ?? false,
                    page ?? 1,
                    pageSize ?? 50,
                    new NodeId(nodeId));

                return await EndpointHelpers.ExecuteAsync(async () =>
                    {
                        var result = await service
                            .RefreshNodeAsync(resourceKind, new NodeId(nodeId), query, cancellationToken)
                            .ConfigureAwait(false);
                        return Results.Ok(result);
                    })
                    .ConfigureAwait(false);
            });

        resources.MapGet(
            "/{kind}/{nodeId:guid}/{**sourceId}",
            async (
                string kind,
                Guid nodeId,
                string sourceId,
                IResourceQueryService service,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseKind(kind, out var resourceKind))
                {
                    return Results.BadRequest(new { message = "未知资源类型。" });
                }

                return await EndpointHelpers.ExecuteAsync(async () =>
                    {
                        var detail = await service
                            .GetDetailAsync(resourceKind, new NodeId(nodeId), sourceId, cancellationToken)
                            .ConfigureAwait(false);
                        return Results.Ok(detail);
                    })
                    .ConfigureAwait(false);
            });

        // 旧资源清单页已与管理页 1:1 合并（docs/19 问题 9）：保留旧地址，服务端 302 到对应管理页，
        // 查询串（nodeId/id 等深链参数）原样透传，避免旧书签失效。
        app.MapGet(
                "/resources/{kind}",
                (string kind, HttpRequest request) =>
                {
                    var target = kind.Trim().ToLowerInvariant() switch
                    {
                        "websites" => "/websites",
                        "cronjobs" => "/cronjobs",
                        "apps" => "/appstore?tab=installed",
                        "containers" => "/containers",
                        "databases" => "/databases",
                        _ => null
                    };

                    if (target is null)
                    {
                        return Results.NotFound();
                    }

                    var query = request.QueryString.Value;
                    if (!string.IsNullOrEmpty(query))
                    {
                        target += (target.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query[1..];
                    }

                    return Results.Redirect(target, permanent: false);
                })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        return app;
    }

    public static bool TryParseKind(string value, out ResourceKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "websites":
                kind = ResourceKind.Websites;
                return true;
            case "apps":
                kind = ResourceKind.Apps;
                return true;
            case "containers":
                kind = ResourceKind.Containers;
                return true;
            case "databases":
                kind = ResourceKind.Databases;
                return true;
            case "cronjobs":
                kind = ResourceKind.Cronjobs;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
