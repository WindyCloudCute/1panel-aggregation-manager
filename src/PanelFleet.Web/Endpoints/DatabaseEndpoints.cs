using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Databases;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>P8 数据库只读查询端点（Admin only）。写操作统一走 /api/operations 内核。</summary>
public static class DatabaseEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        var databases = app.MapGroup("/api/databases").RequireAuthorization(AuthSetup.AdminPolicy);

        databases.MapGet(
            "/",
            async (Guid? nodeId, IDatabaseService service, CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await service.GetOverviewAsync(nodeId, cancellationToken).ConfigureAwait(false));
                }
                catch (DatabaseNodeNotFoundException exception)
                {
                    return Results.NotFound(new { message = exception.Message });
                }
            });

        databases.MapGet(
            "/{nodeId:guid}/instances/{instance}/users",
            async (
                Guid nodeId,
                string instance,
                IDatabaseService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    async () => Results.Ok(await service
                        .GetUsersAsync(nodeId, instance, cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        databases.MapGet(
            "/{nodeId:guid}/instances/{instance}/grants",
            async (
                Guid nodeId,
                string instance,
                IDatabaseService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    async () => Results.Ok(await service
                        .GetGrantsAsync(nodeId, instance, cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        databases.MapGet(
            "/{nodeId:guid}/instances/{instance}/formats",
            async (
                Guid nodeId,
                string instance,
                IDatabaseService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    async () => Results.Ok(await service
                        .GetFormatsAsync(nodeId, instance, cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        databases.MapGet(
            "/{nodeId:guid}/instances/{instance}/status",
            async (
                Guid nodeId,
                string instance,
                string? engine,
                string? from,
                IDatabaseService service,
                CancellationToken cancellationToken) =>
            {
                if (!DatabaseEngines.TryParse(engine, out var parsed))
                {
                    return Results.BadRequest(new { message = "数据库引擎不合法。" });
                }

                return await ExecuteAsync(
                        async () => Results.Ok(await service
                            .GetStatusAsync(nodeId, parsed, instance, from ?? string.Empty, cancellationToken)
                            .ConfigureAwait(false)))
                    .ConfigureAwait(false);
            });

        databases.MapGet(
            "/{nodeId:guid}/instances/{instance}/redis",
            async (
                Guid nodeId,
                string instance,
                string? from,
                IDatabaseService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    async () => Results.Ok(await service
                        .GetRedisInfoAsync(nodeId, instance, from ?? string.Empty, cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        databases.MapGet(
            "/{nodeId:guid}/instances/{instance}/delete-check",
            async (
                Guid nodeId,
                string instance,
                string? engine,
                long? id,
                IDatabaseService service,
                CancellationToken cancellationToken) =>
            {
                if (!DatabaseEngines.TryParse(engine, out var parsed) || id is null or <= 0)
                {
                    return Results.BadRequest(new { message = "数据库引擎或库 id 不合法。" });
                }

                return await ExecuteAsync(
                        async () => Results.Ok(await service
                            .CheckDeleteAsync(nodeId, parsed, id.Value, instance, cancellationToken)
                            .ConfigureAwait(false)))
                    .ConfigureAwait(false);
            });

        return app;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DatabaseNodeNotFoundException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
        catch (DatabaseOperationRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }
}
