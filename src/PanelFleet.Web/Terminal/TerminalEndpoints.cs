using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Terminal;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Terminal;

/// <summary>终端 WS 端点映射（由 Program 调用；需要 <c>app.UseWebSockets()</c>）。</summary>
public static class TerminalEndpoints
{
    /// <summary>浏览器终端 WebSocket 路径。</summary>
    public const string WebSocketPath = "/ws/terminal";

    /// <summary>
    /// 映射 <c>GET /ws/terminal</c>（Admin only）。
    /// 调用要求：Program 已注册认证/授权中间件，并调用过 <c>app.UseWebSockets()</c>。
    /// </summary>
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Map(WebSocketPath, TerminalWebSocketHandler.HandleAsync)
            .RequireAuthorization(AuthSetup.AdminPolicy);

        return app;
    }
}

/// <summary>终端 WS 握手处理：鉴权后校验开关/Origin/参数，再交给会话管理器与桥。</summary>
internal static class TerminalWebSocketHandler
{
    public static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.RequestServices.GetRequiredService<TerminalOptions>();
        if (!options.Enabled)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "终端功能已禁用",
                    "管理员已通过配置项 Terminal:Enabled=false 关闭终端功能。")
                .ConfigureAwait(false);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var host = context.Request.Host.Value ?? string.Empty;
        var originDecision = TerminalOriginPolicy.Evaluate(
            origin,
            host,
            context.Request.Scheme,
            options.AllowMissingOrigin,
            options.AllowedOrigins);
        if (!originDecision.Allowed)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "Origin 校验失败",
                    originDecision.Reason ?? "不允许的 Origin。")
                .ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "需要 WebSocket",
                    "本端点仅支持 WebSocket 升级请求。")
                .ConfigureAwait(false);
            return;
        }

        if (!TryParseRequest(context, out var request, out var validationError))
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "终端参数不合法",
                    validationError ?? "请检查 nodeId / container / cols / rows 等参数。")
                .ConfigureAwait(false);
            return;
        }

        var manager = context.RequestServices.GetRequiredService<ITerminalSessionManager>();
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("PanelFleet.Terminal.Endpoint");
        var actor = ActorFactory.FromPrincipal(context.User, context);

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var channel = new WebSocketTerminalChannel(socket);

        TerminalSessionHandle handle;
        try
        {
            handle = await manager.OpenAsync(request, actor, context.RequestAborted).ConfigureAwait(false);
        }
        catch (TerminalException exception)
        {
            await SendFailureAsync(channel, exception.Code, exception.Message).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "终端会话初始化失败");
            await SendFailureAsync(channel, "internal_error", "终端初始化失败，请稍后重试。").ConfigureAwait(false);
            return;
        }

        try
        {
            await handle.RunAsync(channel, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 浏览器断开/服务器停止，属正常结束路径。
        }
        finally
        {
            try
            {
                if (socket.State is System.Net.WebSockets.WebSocketState.Open
                    or System.Net.WebSockets.WebSocketState.CloseReceived)
                {
                    await socket
                        .CloseAsync(
                            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                            "terminal closed",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogDebug(exception, "关闭终端 WebSocket 时出错");
            }
        }
    }

    private static async Task SendFailureAsync(WebSocketTerminalChannel channel, string code, string message)
    {
        try
        {
            await channel
                .SendTextAsync(TerminalProtocol.EncodeError(code, message), CancellationToken.None)
                .ConfigureAwait(false);
            await channel
                .SendTextAsync(
                    TerminalProtocol.EncodeClosed(TerminalCloseReasons.UpstreamError, message, canReconnect: false),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await channel.CloseAsync(1011, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 浏览器已断开时忽略。
        }
    }

    private static bool TryParseRequest(
        HttpContext context,
        out TerminalSessionRequest request,
        out string? error)
    {
        request = null!;
        error = null;

        var query = context.Request.Query;
        if (!Guid.TryParse(query["nodeId"], out var nodeId))
        {
            error = "缺少或非法的 nodeId（需要 Guid）。";
            return false;
        }

        var container = Normalize(query["container"]);
        if (container is { Length: > 128 })
        {
            error = "container 过长（最多 128 字符）。";
            return false;
        }

        var command = Normalize(query["command"]) ?? "/bin/sh";
        if (command.Length > 128)
        {
            error = "command 过长（最多 128 字符）。";
            return false;
        }

        var user = Normalize(query["user"]) ?? string.Empty;
        if (user.Length > 64)
        {
            error = "user 过长（最多 64 字符）。";
            return false;
        }

        var mode = container is null ? TerminalMode.Host : TerminalMode.Container;
        var cols = ParseInt(query["cols"], defaultValue: 80, min: 20, max: 500);
        var rows = ParseInt(query["rows"], defaultValue: 40, min: 5, max: 200);

        request = new TerminalSessionRequest(
            new NodeId(nodeId),
            mode,
            container,
            mode == TerminalMode.Container ? command : null,
            mode == TerminalMode.Container ? user : null,
            cols,
            rows);
        return true;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(string? value, int defaultValue, int min, int max) =>
        int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : defaultValue;

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail) =>
        Results
            .Problem(title: title, detail: detail, statusCode: statusCode)
            .ExecuteAsync(context);
}
