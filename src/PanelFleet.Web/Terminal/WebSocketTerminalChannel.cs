using System.Net.WebSockets;
using System.Text;
using PanelFleet.Application.Terminal;

namespace PanelFleet.Web.Terminal;

/// <summary>把 ASP.NET Core 的 <see cref="WebSocket"/> 适配为 <see cref="ITerminalChannel"/>。</summary>
public sealed class WebSocketTerminalChannel : ITerminalChannel
{
    private const int MaxBrowserMessageBytes = 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketTerminalChannel(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    public async Task<TerminalChannelMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new TerminalChannelMessage(TerminalChannelMessageKind.Closed);
                }

                if (result.Count > 0)
                {
                    buffer.Write(_buffer, 0, result.Count);
                    if (buffer.Length > MaxBrowserMessageBytes)
                    {
                        return new TerminalChannelMessage(TerminalChannelMessageKind.Faulted);
                    }
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return new TerminalChannelMessage(TerminalChannelMessageKind.Text, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebSocketException)
        {
            return new TerminalChannelMessage(TerminalChannelMessageKind.Faulted);
        }
        catch (ObjectDisposedException)
        {
            return new TerminalChannelMessage(TerminalChannelMessageKind.Faulted);
        }
    }

    public async Task SendTextAsync(string payload, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            await _socket
                .SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(int statusCode, string reason, CancellationToken cancellationToken)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            var closeStatus = statusCode is 1000
                ? WebSocketCloseStatus.NormalClosure
                : (WebSocketCloseStatus)Math.Clamp(statusCode, 1000, 4999);
            await _socket
                .CloseOutputAsync(closeStatus, Truncate(reason), timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 浏览器连接已断开时忽略关闭失败。
        }
    }

    private static string Truncate(string reason) =>
        reason.Length <= 100 ? reason : reason[..100];
}
