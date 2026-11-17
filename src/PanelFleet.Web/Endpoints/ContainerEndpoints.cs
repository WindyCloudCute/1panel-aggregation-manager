using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Containers;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>P6 容器只读查询端点（Admin only）。写操作统一走 /api/operations 内核。</summary>
public static class ContainerEndpoints
{
    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder app)
    {
        var containers = app.MapGroup("/api/containers").RequireAuthorization(AuthSetup.AdminPolicy);

        containers.MapGet(
            "/",
            async (
                string? name,
                string? state,
                string? orderBy,
                string? order,
                int? page,
                int? pageSize,
                Guid? nodeId,
                IContainerService service,
                CancellationToken cancellationToken) =>
            {
                var query = new ContainerQuery(
                    nodeId,
                    name,
                    state ?? "all",
                    orderBy ?? "name",
                    order ?? "ascending",
                    page ?? 1,
                    pageSize ?? 50);

                try
                {
                    return Results.Ok(await service.ListAsync(query, cancellationToken).ConfigureAwait(false));
                }
                catch (ContainerNodeNotFoundException exception)
                {
                    return Results.NotFound(new { message = exception.Message });
                }
            });

        containers.MapGet(
            "/compose",
            async (
                string? name,
                int? page,
                int? pageSize,
                Guid? nodeId,
                IContainerService service,
                CancellationToken cancellationToken) =>
            {
                var query = new ContainerQuery(nodeId, name, "all", "name", "ascending", page ?? 1, pageSize ?? 50);
                try
                {
                    return Results.Ok(await service.ListComposeAsync(query, cancellationToken).ConfigureAwait(false));
                }
                catch (ContainerNodeNotFoundException exception)
                {
                    return Results.NotFound(new { message = exception.Message });
                }
            });

        containers.MapGet(
            "/compose/content",
            async (
                string? name,
                Guid? nodeId,
                IComposeContentService service,
                CancellationToken cancellationToken) =>
            {
                if (nodeId is null || string.IsNullOrWhiteSpace(name))
                {
                    return Results.BadRequest(new { message = "需要 nodeId 与 name 参数。" });
                }

                try
                {
                    return Results.Ok(await service
                        .GetComposeContentAsync(nodeId.Value, name, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (ContainerNodeNotFoundException exception)
                {
                    return Results.NotFound(new { message = exception.Message });
                }
                catch (ContainerComposeNotFoundException exception)
                {
                    return Results.NotFound(new { message = exception.Message });
                }
                catch (ComposeContentUnsupportedException exception)
                {
                    return Results.BadRequest(new { message = exception.Message });
                }
            });

        containers.MapGet(
            "/{nodeId:guid}/{containerName}/detail",
            async (
                Guid nodeId,
                string containerName,
                IContainerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    async () => Results.Ok(await service
                        .GetDetailAsync(nodeId, containerName, cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        containers.MapGet(
            "/{nodeId:guid}/{containerName}/stats",
            async (
                Guid nodeId,
                string containerName,
                string? containerId,
                IContainerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    async () => Results.Ok(await service
                        .GetStatsAsync(nodeId, containerName, containerId, cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        containers.MapGet(
            "/{nodeId:guid}/{containerName}/logs",
            async (
                Guid nodeId,
                string containerName,
                string? since,
                int? tail,
                bool? timestamp,
                IContainerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    async () => Results.Ok(await service
                        .GetLogsAsync(
                            nodeId,
                            new ContainerLogRequest(containerName, since ?? "all", tail ?? 200, timestamp ?? false),
                            cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        return app;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ContainerNodeNotFoundException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
    }
}
