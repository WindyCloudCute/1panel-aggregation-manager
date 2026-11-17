namespace PanelFleet.Web.Components.Terminal;

/// <summary>终端组件向页面回报的事件（来自 JS 互操作，camelCase JSON）。</summary>
public sealed class TerminalEvent
{
    /// <summary>事件类型：mode / status / ready / error / closed / latency。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>status 事件的连接状态：connecting / connected / closed / error。</summary>
    public string? Status { get; set; }

    /// <summary>mode 事件：xterm / simple。</summary>
    public string? Mode { get; set; }

    public string? NodeName { get; set; }

    public string? Container { get; set; }

    /// <summary>error 事件的稳定错误码。</summary>
    public string? Code { get; set; }

    public string? Message { get; set; }

    public string? Reason { get; set; }

    public bool CanReconnect { get; set; }

    /// <summary>latency 事件的往返毫秒数。</summary>
    public double? Ms { get; set; }
}
