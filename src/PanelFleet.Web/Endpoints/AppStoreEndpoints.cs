using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application;
using PanelFleet.Application.AppStore;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>应用商店搜索请求（只读，POST 仅因 1Panel 使用 POST 检索）。</summary>
public sealed record AppStoreSearchRequest(
    Guid NodeId,
    string? Name,
    string[]? Tags,
    string? Type,
    string? Resource,
    bool? ShowCurrentArch,
    int? Page,
    int? PageSize);

/// <summary>已安装应用搜索请求（只读）。</summary>
public sealed record InstalledAppSearchRequest(
    Guid NodeId,
    string? Name,
    string[]? Tags,
    bool? Update,
    bool? CheckUpdate,
    int? Page,
    int? PageSize);

/// <summary>P5 应用商店只读查询 HTTP 端点（写操作统一走 <c>/api/operations</c> 内核）。</summary>
public static class AppStoreEndpoints
{
    public static IEndpointRouteBuilder MapAppStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/api/appstore").RequireAuthorization(AuthSetup.AdminPolicy);

        store.MapGet(
            "/tags",
            (Guid nodeId, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Ok(
                    await service.GetTagsAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false))));

        store.MapPost(
            "/search",
            async (
                AppStoreSearchRequest request,
                AppStoreService service,
                HttpContext context,
                IAntiforgery antiforgery,
                CancellationToken cancellationToken) =>
            {
                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                var query = new AppStoreQuery(
                    request.Name,
                    request.Tags ?? [],
                    request.Type,
                    request.Resource,
                    request.ShowCurrentArch ?? false,
                    request.Page ?? 1,
                    request.PageSize ?? 12);

                return await ExecuteAsync(async () => Results.Ok(
                        await service.SearchAsync(new NodeId(request.NodeId), query, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false);
            });

        store.MapGet(
            "/apps/{key}",
            (string key, Guid nodeId, string? version, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Ok(
                    await service.GetDetailAsync(new NodeId(nodeId), key, version, cancellationToken).ConfigureAwait(false))));

        store.MapPost(
            "/installed/search",
            async (
                InstalledAppSearchRequest request,
                AppStoreService service,
                HttpContext context,
                IAntiforgery antiforgery,
                CancellationToken cancellationToken) =>
            {
                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                return await ExecuteAsync(async () => Results.Ok(
                        await service.SearchInstalledAsync(
                                new NodeId(request.NodeId),
                                request.Name,
                                request.Tags ?? [],
                                request.Update ?? false,
                                request.CheckUpdate ?? false,
                                request.Page ?? 1,
                                request.PageSize ?? 20,
                                cancellationToken)
                            .ConfigureAwait(false)))
                    .ConfigureAwait(false);
            });

        store.MapGet(
            "/installed/{installId:long}",
            (long installId, Guid nodeId, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Ok(
                    await service.GetInstalledAsync(new NodeId(nodeId), installId, cancellationToken).ConfigureAwait(false))));

        store.MapGet(
            "/installed/{installId:long}/params",
            (long installId, Guid nodeId, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Ok(
                    await service.GetInstalledParamsAsync(new NodeId(nodeId), installId, cancellationToken).ConfigureAwait(false))));

        store.MapGet(
            "/installed/{installId:long}/update-versions",
            (long installId, Guid nodeId, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Ok(
                    await service.GetUpdateVersionsAsync(new NodeId(nodeId), installId, cancellationToken).ConfigureAwait(false))));

        store.MapGet(
            "/installed/{installId:long}/delete-check",
            (long installId, Guid nodeId, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Ok(
                    await service.GetDeleteCheckAsync(new NodeId(nodeId), installId, cancellationToken).ConfigureAwait(false))));

        store.MapGet(
            "/tasks/{taskId}",
            (string taskId, Guid nodeId, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () =>
                {
                    var status = await service
                        .GetTaskStatusAsync(new NodeId(nodeId), taskId, cancellationToken)
                        .ConfigureAwait(false);
                    return status is null
                        ? Results.NotFound(new { message = "任务不存在或已被清理。" })
                        : Results.Ok(status);
                }));

        store.MapGet(
            "/checkupdate",
            (Guid nodeId, AppStoreService service, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Ok(
                    await service.CheckStoreUpdateAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false))));

        return app;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (NodeNotFoundException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
        catch (AppStoreException exception)
        {
            var statusCode = exception.Error.Kind switch
            {
                PanelErrorKind.Validation => StatusCodes.Status400BadRequest,
                PanelErrorKind.NotFound => StatusCodes.Status404NotFound,
                PanelErrorKind.Canceled => StatusCodes.Status408RequestTimeout,
                _ => StatusCodes.Status502BadGateway
            };

            return Results.Problem(
                title: "应用商店请求失败",
                detail: exception.Error.Message,
                statusCode: statusCode);
        }
    }
}
