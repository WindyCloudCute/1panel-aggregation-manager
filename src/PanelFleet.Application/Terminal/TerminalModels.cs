using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Terminal;

/// <summary>终端模式。</summary>
public enum TerminalMode
{
    /// <summary>主机终端（1Panel 本机 shell，经本机 SSH 打开）。</summary>
    Host = 0,

    /// <summary>容器 exec 终端。</summary>
    Container = 1
}

/// <summary>终端会话状态。</summary>
public enum TerminalSessionState
{
    Connecting = 0,
    Active = 1,
    Revalidating = 2,
    Closed = 3,
    Failed = 4
}

/// <summary>打开终端的请求（Web 层校验 query 后构造）。</summary>
public sealed record TerminalSessionRequest(
    NodeId NodeId,
    TerminalMode Mode,
    string? Container,
    string? ContainerCommand,
    string? ContainerUser,
    int Cols,
    int Rows);

/// <summary>对外可见的会话信息（不含任何密钥/输入内容）。</summary>
public sealed record TerminalSessionInfo(
    string SessionId,
    NodeId NodeId,
    string NodeName,
    TerminalMode Mode,
    string? Container,
    DateTimeOffset StartedAt,
    int Cols,
    int Rows)
{
    public TerminalSessionState State { get; init; } = TerminalSessionState.Connecting;
}

/// <summary>一次会话的量化指标（审计用；不含内容）。</summary>
public sealed class TerminalSessionTelemetry
{
    private int _inputFrames;
    private int _revalidateCount;
    private long _inputBytes;
    private long _outputBytes;

    public int InputFrames => Volatile.Read(ref _inputFrames);

    public int RevalidateCount => Volatile.Read(ref _revalidateCount);

    public long InputBytes => Volatile.Read(ref _inputBytes);

    public long OutputBytes => Volatile.Read(ref _outputBytes);

    public void AddInput(int bytes)
    {
        Interlocked.Increment(ref _inputFrames);
        Interlocked.Add(ref _inputBytes, bytes);
    }

    public void AddOutput(int bytes) => Interlocked.Add(ref _outputBytes, bytes);

    public void IncrementRevalidate() => Interlocked.Increment(ref _revalidateCount);
}

/// <summary>桥接运行参数。</summary>
public sealed record TerminalLimits(
    TimeSpan IdleTimeout,
    TimeSpan MaxDuration,
    int MaxFrameBytes,
    int MaxInputFramesPerSecond,
    int MaxInputBytesPerSecond,
    int RevalidationBufferBytes = 131072,
    int RevalidationDelayMs = 500)
{
    public static TerminalLimits Default { get; } = new(
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(240),
        65536,
        30,
        131072);

    public static TerminalLimits FromOptions(TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TerminalLimits(
            options.IdleTimeout,
            options.MaxSessionDuration,
            options.MaxFrameBytes,
            options.MaxInputFramesPerSecond,
            options.MaxInputBytesPerSecond);
    }
}

/// <summary>连接 1Panel 终端 WS 所需的上下文。</summary>
public sealed record TerminalConnectContext(
    NodeDescriptor Node,
    TerminalMode Mode,
    string? Container,
    string? ContainerCommand,
    string? ContainerUser,
    int Cols,
    int Rows)
{
    /// <summary>预检时已解析出的 1Panel 鉴权方式（Auto 且无法判断时为 null）。</summary>
    public AuthMode? ResolvedAuthMode { get; init; }
}

/// <summary>关闭原因常量。</summary>
public static class TerminalCloseReasons
{
    public const string User = "user";
    public const string Idle = "idle";
    public const string MaxDuration = "max_duration";
    public const string UpstreamClosed = "upstream_closed";
    public const string UpstreamError = "upstream_error";
    public const string RevalidateFailed = "revalidate_failed";
    public const string InputLimit = "input_limit";
    public const string Server = "server";
}

/// <summary>桥接结束原因（用于审计与浏览器提示）。</summary>
public sealed record TerminalCloseOutcome(string Reason, string? Message, bool CanReconnect)
{
    public static TerminalCloseOutcome User { get; } = new(TerminalCloseReasons.User, null, true);

    public static TerminalCloseOutcome Idle { get; } = new(
        TerminalCloseReasons.Idle, "会话空闲超时，已自动断开。", true);

    public static TerminalCloseOutcome MaxDuration { get; } = new(
        TerminalCloseReasons.MaxDuration, "会话达到最长时长，已自动断开。", true);
}

/// <summary>终端相关业务异常（Web 层映射为 HTTP 状态码或错误帧）。</summary>
public class TerminalException : Exception
{
    public TerminalException(string code, string message, int statusCode = 400, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
    }

    /// <summary>稳定错误码（浏览器 error.code 与文档一致）。</summary>
    public string Code { get; }

    /// <summary>升级前校验失败时使用的 HTTP 状态码。</summary>
    public int StatusCode { get; }
}

/// <summary>参数/状态校验失败（400）。</summary>
public sealed class TerminalValidationException : TerminalException
{
    public TerminalValidationException(string code, string message, Exception? innerException = null)
        : base(code, message, 400, innerException)
    {
    }
}

/// <summary>终端功能被关闭（403）。</summary>
public sealed class TerminalDisabledException : TerminalException
{
    public TerminalDisabledException()
        : base("terminal_disabled", "终端功能已禁用（Terminal:Enabled=false）。", 403)
    {
    }
}

/// <summary>节点/容器不存在（404）。</summary>
public sealed class TerminalNotFoundException : TerminalException
{
    public TerminalNotFoundException(string code, string message)
        : base(code, message, 404)
    {
    }
}

/// <summary>并发会话达到上限（409）。</summary>
public sealed class TerminalCapacityException : TerminalException
{
    public TerminalCapacityException(int limit)
        : base("capacity", $"当前终端会话数已达上限（{limit}），请先关闭其它终端。", 409)
    {
    }
}

/// <summary>连接或使用 1Panel 终端失败（502）。</summary>
public sealed class TerminalUpstreamException : TerminalException
{
    public TerminalUpstreamException(string code, string message, Exception? innerException = null)
        : base(code, message, 502, innerException)
    {
    }
}
