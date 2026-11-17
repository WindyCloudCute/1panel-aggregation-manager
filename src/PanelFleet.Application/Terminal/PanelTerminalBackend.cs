using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Terminal;

/// <summary>
/// 真实的 1Panel 终端后端：使用 BCL <see cref="ClientWebSocket"/>，
/// 自行拼装 1Panel 的 API Key 签名头（REST 与 WS 共用）。
/// </summary>
public sealed class PanelTerminalBackend : ITerminalBackend
{
    private const int MaxServerMessageBytes = 4 * 1024 * 1024;
    private const string TokenHeader = "1Panel-Token";
    private const string TimestampHeader = "1Panel-Timestamp";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly TerminalAuthCache _authCache;
    private readonly TimeProvider _time;
    private readonly ILogger<PanelTerminalBackend> _logger;

    public PanelTerminalBackend(
        TerminalAuthCache authCache,
        TimeProvider time,
        ILogger<PanelTerminalBackend> logger)
    {
        _authCache = authCache;
        _time = time;
        _logger = logger;
    }

    public async Task<ITerminalConnection> ConnectAsync(
        TerminalConnectContext context,
        string? sessionId,
        bool revalidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var candidates = ResolveCandidates(context);
        TerminalException? lastAuthError = null;

        foreach (var mode in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var connection = await ConnectOnceAsync(context, mode, sessionId, revalidate, cancellationToken)
                    .ConfigureAwait(false);
                _authCache.Remember(context.Node.Id, mode);
                return connection;
            }
            catch (TerminalAuthHandshakeException exception)
            {
                lastAuthError = new TerminalUpstreamException(
                    "upstream_auth_failed",
                    "1Panel 终端鉴权失败：请检查 API Key、接口状态与 IP 白名单。",
                    exception);
                _logger.LogInformation(
                    "节点 {NodeId} 终端 WS 握手鉴权失败（{AuthMode}）",
                    context.Node.Id,
                    mode);
            }
        }

        throw lastAuthError ?? new TerminalUpstreamException(
            "upstream_auth_failed",
            "1Panel 终端鉴权失败，请检查节点配置。");
    }

    private List<AuthMode> ResolveCandidates(TerminalConnectContext context)
    {
        var visited = new HashSet<AuthMode>();
        var candidates = new List<AuthMode>(2);
        var node = context.Node;

        if (node.AuthMode is AuthMode.HmacSha256 or AuthMode.Md5)
        {
            candidates.Add(node.AuthMode);
            return candidates;
        }

        if (context.ResolvedAuthMode is AuthMode resolved && candidates.Count == 0)
        {
            if (visited.Add(resolved))
            {
                candidates.Add(resolved);
            }
        }

        if (_authCache.TryGet(node.Id) is { } cached && visited.Add(cached))
        {
            candidates.Add(cached);
        }

        foreach (var fallback in new[] { AuthMode.HmacSha256, AuthMode.Md5 })
        {
            if (visited.Add(fallback))
            {
                candidates.Add(fallback);
            }
        }

        return candidates;
    }

    private async Task<ITerminalConnection> ConnectOnceAsync(
        TerminalConnectContext context,
        AuthMode mode,
        string? sessionId,
        bool revalidate,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            ConfigureTls(socket, context.Node.TlsPolicy);

            var unixSeconds = GetUnixSeconds(context.Node);
            socket.Options.SetRequestHeader(TokenHeader, TerminalSignature.Compute(context.Node.ApiKey, unixSeconds, mode));
            socket.Options.SetRequestHeader(
                TimestampHeader,
                unixSeconds.ToString(CultureInfo.InvariantCulture));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);

            var uri = TerminalRoutes.BuildTerminalUri(context, sessionId, revalidate);
            try
            {
                await socket.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TerminalUpstreamException(
                    "upstream_unreachable",
                    "连接 1Panel 终端超时，请检查节点地址与网络。");
            }
            catch (WebSocketException exception) when (IsHandshakeFailure(exception))
            {
                throw new TerminalAuthHandshakeException("1Panel 终端握手鉴权失败。", exception);
            }
            catch (WebSocketException exception)
            {
                throw new TerminalUpstreamException(
                    "upstream_unreachable",
                    "无法连接 1Panel 终端，请检查节点地址、TLS 与网络。",
                    exception);
            }

            _logger.LogInformation(
                "节点 {NodeId} 终端 WS 已连接（{Mode}，鉴权 {AuthMode}）",
                context.Node.Id,
                context.Mode,
                mode);

            return new PanelTerminalConnection(socket, context.Node.Id);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void ConfigureTls(ClientWebSocket socket, TlsPolicy tlsPolicy)
    {
        if (tlsPolicy == TlsPolicy.AllowSelfSigned)
        {
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }
    }

    private long GetUnixSeconds(NodeDescriptor node)
    {
        var now = _time.GetUtcNow();
        var adjusted = node.ClockOffset == default ? now : now + node.ClockOffset;
        return adjusted.ToUnixTimeSeconds();
    }

    /// <summary>识别「HTTP 握手未返回 101」类失败（1Panel 鉴权失败表现为 HTTP 200 + 业务 code 401）。</summary>
    private static bool IsHandshakeFailure(WebSocketException exception) =>
        exception.WebSocketErrorCode is WebSocketError.Success or WebSocketError.NotAWebSocket;

    private sealed class TerminalAuthHandshakeException : Exception
    {
        public TerminalAuthHandshakeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>真实 WS 连接：文本 JSON 帧收发。</summary>
    private sealed class PanelTerminalConnection : ITerminalConnection
    {
        private readonly ClientWebSocket _socket;
        private readonly NodeId _nodeId;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly byte[] _receiveBuffer = new byte[16 * 1024];
        private string? _sessionId;
        private bool _disposed;

        public PanelTerminalConnection(ClientWebSocket socket, NodeId nodeId)
        {
            _socket = socket;
            _nodeId = nodeId;
        }

        public NodeId NodeId => _nodeId;

        public string? SessionId => _sessionId;

        public async Task<TerminalServerFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(_receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new TerminalServerFrame(
                        TerminalServerFrameKind.Closed,
                        CloseCode: (int?)_socket.CloseStatus,
                        CloseReason: _socket.CloseStatusDescription);
                }

                if (result.Count > 0)
                {
                    buffer.Write(_receiveBuffer, 0, result.Count);
                    if (buffer.Length > MaxServerMessageBytes)
                    {
                        throw new TerminalUpstreamException(
                            "upstream_error",
                            "1Panel 终端返回的消息过大，已断开连接。");
                    }
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            var payload = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var frame = TerminalProtocol.ParseServerFrame(payload);
            if (frame.Kind == TerminalServerFrameKind.Session && !string.IsNullOrEmpty(frame.SessionId))
            {
                _sessionId = frame.SessionId;
            }

            return frame;
        }

        public Task SendInputAsync(byte[] data, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(data);
            return SendTextAsync(TerminalProtocol.EncodeInput(data), cancellationToken);
        }

        public Task SendResizeAsync(int cols, int rows, CancellationToken cancellationToken) =>
            SendTextAsync(TerminalProtocol.EncodeResize(cols, rows), cancellationToken);

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket
                        .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "panel-fleet-close", timeoutCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                // 关闭失败不影响后续释放。
            }
            finally
            {
                Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Task.CompletedTask.ConfigureAwait(false);
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _socket.Dispose();
            _sendLock.Dispose();
        }

        private async Task SendTextAsync(string payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                await _socket
                    .SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
