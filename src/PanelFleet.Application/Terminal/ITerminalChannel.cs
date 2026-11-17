namespace PanelFleet.Application.Terminal;

/// <summary>浏览器通道消息类型。</summary>
public enum TerminalChannelMessageKind
{
    /// <summary>文本帧（JSON）。</summary>
    Text = 0,

    /// <summary>浏览器发送了关闭帧（正常关闭）。</summary>
    Closed = 1,

    /// <summary>浏览器连接异常中断。</summary>
    Faulted = 2
}

/// <summary>浏览器通道收到的一条消息。</summary>
public sealed record TerminalChannelMessage(TerminalChannelMessageKind Kind, string? Text = null);

/// <summary>
/// 浏览器侧终端通道抽象。Web 层用真实 WebSocket 实现，
/// 单元测试用内存假通道，桥接逻辑（<see cref="TerminalBridge"/>）只依赖本接口。
/// </summary>
public interface ITerminalChannel
{
    Task<TerminalChannelMessage> ReceiveAsync(CancellationToken cancellationToken);

    Task SendTextAsync(string payload, CancellationToken cancellationToken);

    Task CloseAsync(int statusCode, string reason, CancellationToken cancellationToken);
}
