using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using PanelFleet.Application;

namespace PanelFleet.Web.Endpoints;

internal static class EndpointHelpers
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (NodeValidationException exception)
        {
            return Results.ValidationProblem(exception.Errors.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        catch (NodeConflictException exception)
        {
            return Results.Problem(
                title: "资源冲突",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (NodeNotFoundException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
        catch (NodeGroupNotFoundException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
    }
}

internal static class Csrf
{
    public static async Task<bool> ValidateAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    public static IResult Reject() =>
        Results.Problem(
            title: "CSRF 校验失败",
            detail: "缺少或无效的防伪令牌，请刷新页面后重试。",
            statusCode: StatusCodes.Status400BadRequest);
}
