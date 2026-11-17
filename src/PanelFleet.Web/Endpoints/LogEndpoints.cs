using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Logs;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>
/// P10 系统日志只读端点：
/// <list type="bullet">
/// <item><description><c>GET /api/logs</c>：按范围查询（panel/system/container/website）；</description></item>
/// <item><description><c>GET /api/logs/filters</c>：系统日志可用服务与文件日期。</description></item>
/// </list>
/// 不提供清空/下载/实时跟随等写操作。
/// </summary>
public static class LogEndpoints
{
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        var logs = app.MapGroup("/api/logs").RequireAuthorization(AuthSetup.AdminPolicy);

        logs.MapGet(
            "",
            async (
                string? type,
                Guid? nodeId,
                string? container,
                string? website,
                string? panelLog,
                string? logType,
                DateTimeOffset? start,
                DateTimeOffset? end,
                string? keyword,
                int? limit,
                string? service,
                string? priority,
                string? cursor,
                ILogService logService,
                CancellationToken cancellationToken) =>
            {
                if (!LogScopes.TryParse(type, out var scope))
                {
                    return Results.BadRequest(new
                    {
                        message = "未知日志范围 type，支持 panel/system/container/website。"
                    });
                }

                var query = new LogQuery(
                    scope,
                    nodeId,
                    container,
                    website,
                    panelLog ?? "operation",
                    logType ?? "access",
                    start,
                    end,
                    keyword,
                    limit,
                    service,
                    priority,
                    cursor);

                try
                {
                    return Results.Ok(await logService
                        .QueryAsync(query, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (LogValidationException exception)
                {
                    return Results.BadRequest(new { message = exception.Message });
                }
                catch (LogNodeNotFoundException exception)
                {
                    return Results.NotFound(new { message = exception.Message });
                }
            });

        logs.MapGet(
            "/filters",
            async (Guid? nodeId, ILogService logService, CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await logService
                        .GetFilterOptionsAsync(nodeId, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (LogValidationException exception)
                {
                    return Results.BadRequest(new { message = exception.Message });
                }
                catch (LogNodeNotFoundException exception)
                {
                    return Results.NotFound(new { message = exception.Message });
                }
            });

        return app;
    }
}
