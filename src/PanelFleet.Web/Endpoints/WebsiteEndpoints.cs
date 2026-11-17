using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application;
using PanelFleet.Application.Websites;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>P7 网站管理只读查询端点（Admin only）。写操作统一走 /api/operations 内核。</summary>
public static class WebsiteEndpoints
{
    public static IEndpointRouteBuilder MapWebsiteEndpoints(this IEndpointRouteBuilder app)
    {
        var websites = app.MapGroup("/api/websites").RequireAuthorization(AuthSetup.AdminPolicy);

        websites.MapGet(
            "/",
            async (
                Guid? nodeId,
                long? groupId,
                string? name,
                int? page,
                int? pageSize,
                WebsiteService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service
                            .ListAsync(new WebsiteQuery(nodeId, groupId, name, page ?? 1, pageSize ?? 20), cancellationToken)
                            .ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/groups",
            async (Guid? nodeId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetGroupsAsync(nodeId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/ssl",
            async (Guid? nodeId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetSslCertificatesAsync(nodeId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/php-runtimes",
            async (Guid? nodeId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetPhpRuntimesAsync(nodeId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/{nodeId:guid}/create-options",
            async (Guid nodeId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetCreateOptionsAsync(nodeId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/{nodeId:guid}/precheck",
            async (Guid nodeId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.PrecheckAsync(nodeId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/{nodeId:guid}/acme-accounts",
            async (Guid nodeId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetAcmeAccountsAsync(nodeId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/{nodeId:guid}/{websiteId:long}",
            async (Guid nodeId, long websiteId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetDetailAsync(nodeId, websiteId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/{nodeId:guid}/{websiteId:long}/domains",
            async (Guid nodeId, long websiteId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetDomainsAsync(nodeId, websiteId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/{nodeId:guid}/{websiteId:long}/proxies",
            async (Guid nodeId, long websiteId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetProxiesAsync(nodeId, websiteId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        websites.MapGet(
            "/{nodeId:guid}/{websiteId:long}/https",
            async (Guid nodeId, long websiteId, WebsiteService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(
                        await service.GetHttpsAsync(nodeId, websiteId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

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
        catch (WebsiteException exception)
        {
            var statusCode = exception.Error.Kind switch
            {
                PanelErrorKind.Validation => StatusCodes.Status400BadRequest,
                PanelErrorKind.NotFound => StatusCodes.Status404NotFound,
                PanelErrorKind.Canceled => StatusCodes.Status408RequestTimeout,
                _ => StatusCodes.Status502BadGateway
            };

            return Results.Problem(
                title: "网站管理请求失败",
                detail: exception.Error.Message,
                statusCode: statusCode);
        }
    }
}
