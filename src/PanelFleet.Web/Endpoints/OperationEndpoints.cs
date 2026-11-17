using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Operations;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

public sealed record OperationPreviewRequest(string Kind, Guid[]? NodeIds, JsonObject? Parameters);

public sealed record OperationExecuteRequest(string? TypedConfirmation);

public static class OperationEndpoints
{
    public static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var operations = app.MapGroup("/api/operations").RequireAuthorization(AuthSetup.AdminPolicy);

        operations.MapPost(
            "/preview",
            async (
                OperationPreviewRequest request,
                IWriteOperationService service,
                IActorProvider actorProvider,
                HttpContext context,
                IAntiforgery antiforgery,
                CancellationToken cancellationToken) =>
            {
                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                var actor = await actorProvider.GetAsync().ConfigureAwait(false);

                try
                {
                    var invocation = new OperationInvocation(
                        request.Kind,
                        (request.NodeIds ?? []).Select(id => new NodeId(id)).ToArray(),
                        request.Parameters);

                    return Results.Ok(await service.PreviewAsync(invocation, actor, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationValidationException exception)
                {
                    return Results.BadRequest(new { message = exception.Message });
                }
                catch (OperationNotSupportedException exception)
                {
                    return Results.BadRequest(new { message = exception.Message });
                }
            });

        operations.MapPost(
            "/{tokenId:guid}/execute",
            async (
                Guid tokenId,
                OperationExecuteRequest request,
                IWriteOperationService service,
                IActorProvider actorProvider,
                HttpContext context,
                IAntiforgery antiforgery,
                CancellationToken cancellationToken) =>
            {
                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                var actor = await actorProvider.GetAsync().ConfigureAwait(false);

                try
                {
                    var execution = await service
                        .ExecuteAsync(tokenId, request.TypedConfirmation, actor, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(execution);
                }
                catch (OperationConfirmationException exception)
                {
                    return Results.Problem(
                        title: "确认失败",
                        detail: exception.Message,
                        statusCode: StatusCodes.Status409Conflict);
                }
                catch (OperationValidationException exception)
                {
                    return Results.BadRequest(new { message = exception.Message });
                }
                catch (OperationNotSupportedException exception)
                {
                    return Results.BadRequest(new { message = exception.Message });
                }
            });

        operations.MapGet(
            "/",
            (int? take, IWriteOperationService service) => Results.Ok(service.ListRecent(take ?? 20)));

        operations.MapGet(
            "/{id:guid}",
            (Guid id, IWriteOperationService service) =>
                service.GetExecution(id) is { } execution
                    ? Results.Ok(execution)
                    : Results.NotFound(new { message = "操作记录不存在或已从内存历史中淘汰。" }));

        return app;
    }
}
