namespace PanelFleet.Application.Terminal;

/// <summary>
/// 1Panel 终端 WS 后端抽象。桥接逻辑（<see cref="TerminalBridge"/>）只依赖本接口，
/// 单元测试可用假实现替换真实网络。
/// </summary>
public interface ITerminalBackend
{
    /// <summary>
    /// 建立到 1Panel 的终端 WebSocket 连接。
    /// </summary>
    /// <param name="context">连接上下文（节点、模式、容器、尺寸）。</param>
    /// <param name="sessionId">重连时的 1Panel 会话 id；新会话为 null。</param>
    /// <param name="revalidate">是否以「授权重校验」方式重连（对应 <c>terminalRevalidate=1</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ITerminalConnection> ConnectAsync(
        TerminalConnectContext context,
        string? sessionId,
        bool revalidate,
        CancellationToken cancellationToken);
}

/// <summary>一条 1Panel 终端连接（已升级成功）。</summary>
public interface ITerminalConnection : IAsyncDisposable
{
    /// <summary>1Panel 会话 id（收到 <c>session</c> 帧后可用）。</summary>
    string? SessionId { get; }

    /// <summary>接收一条服务端消息（已解析为帧；关闭时返回 <see cref="TerminalServerFrameKind.Closed"/>）。</summary>
    Task<TerminalServerFrame> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>发送键盘输入字节。</summary>
    Task SendInputAsync(byte[] data, CancellationToken cancellationToken);

    /// <summary>发送窗口尺寸。</summary>
    Task SendResizeAsync(int cols, int rows, CancellationToken cancellationToken);

    /// <summary>向上游发送正常关闭（1000）并释放连接。</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
