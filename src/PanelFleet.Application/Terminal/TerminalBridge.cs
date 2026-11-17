using Microsoft.Extensions.Logging;

namespace PanelFleet.Application.Terminal;

/// <summary>
/// 单个终端会话的双向桥：
/// 浏览器通道 ⇄ <see cref="ITerminalBackend"/> 提供的 1Panel 连接。
/// 负责输出转发、输入转发、窗口尺寸、空闲/最长时长、输入限流、
/// 以及对 1Panel「每 60 秒 4410 授权重校验」的透明重连续租。
/// </summary>
public sealed class TerminalBridge
{
    private const int RevalidateCloseCode = 4410;
    private const int SessionNotFoundCloseCode = 4404;
    private const int AttachedElsewhereCloseCode = 4409;

    private readonly ITerminalBackend _backend;
    private readonly TerminalConnectContext _context;
    private readonly TerminalSessionInfo _info;
    private readonly TerminalLimits _limits;
    private readonly TimeProvider _time;
    private readonly ILogger<TerminalBridge> _logger;
    private readonly TerminalInputLimiter _limiter;

    private readonly object _inputGate = new();
    private readonly List<byte[]> _pendingInputs = [];

    private ITerminalConnection? _upstream;
    private bool _revalidating;
    private long _lastActivityTicks;
    private int _pendingInputBytes;
    private string? _errorCode;

    public TerminalBridge(
        ITerminalBackend backend,
        TerminalConnectContext context,
        TerminalSessionInfo info,
        TerminalLimits limits,
        TimeProvider time,
        ILogger<TerminalBridge> logger)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _backend = backend;
        _context = context;
        _info = info;
        _limits = limits;
        _time = time;
        _logger = logger;
        _limiter = new TerminalInputLimiter(limits, time);
    }

    /// <summary>运行桥接，直到会话结束；返回结束原因（用于审计与浏览器提示）。</summary>
    public async Task<TerminalCloseOutcome> RunAsync(
        ITerminalChannel browser,
        TerminalSessionTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(telemetry);

        var startedAt = _time.GetUtcNow();
        Interlocked.Exchange(ref _lastActivityTicks, startedAt.UtcTicks);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = sessionCts.Token;

        try
        {
            var connection = await _backend
                .ConnectAsync(_context, sessionId: null, revalidate: false, token)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _upstream, connection);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
        }
        catch (TerminalException exception)
        {
            _errorCode = exception.Code;
            var outcome = new TerminalCloseOutcome(TerminalCloseReasons.UpstreamError, exception.Message, false);
            await FinalizeAsync(browser, outcome, sendClosed: true).ConfigureAwait(false);
            return outcome;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "终端 {SessionId} 连接 1Panel 失败", _info.SessionId);
            _errorCode = "upstream_unreachable";
            const string message = "无法连接 1Panel 终端，请检查节点状态与网络。";
            var outcome = new TerminalCloseOutcome(TerminalCloseReasons.UpstreamError, message, false);
            await FinalizeAsync(browser, outcome, sendClosed: true).ConfigureAwait(false);
            return outcome;
        }

        var ready = TerminalProtocol.EncodeReady(
            _info.SessionId,
            _info.Mode,
            _info.NodeId.Value.ToString("D"),
            _info.NodeName,
            _info.Container,
            _info.Cols,
            _info.Rows);

        if (!await TrySendAsync(browser, ready, token).ConfigureAwait(false))
        {
            var outcome = new TerminalCloseOutcome(TerminalCloseReasons.User, "浏览器连接已断开。", false);
            await FinalizeAsync(browser, outcome, sendClosed: false).ConfigureAwait(false);
            return outcome;
        }

        var upstreamTask = PumpUpstreamAsync(browser, telemetry, token);
        var browserTask = PumpBrowserAsync(browser, telemetry, token);
        var monitorTask = MonitorAsync(startedAt, token);

        var winner = await Task.WhenAny(upstreamTask, browserTask, monitorTask).ConfigureAwait(false);
        var outcomeResult = await winner.ConfigureAwait(false);

        await sessionCts.CancelAsync().ConfigureAwait(false);
        await IgnoreAsync(upstreamTask, browserTask, monitorTask).ConfigureAwait(false);

        await FinalizeAsync(browser, outcomeResult, sendClosed: true).ConfigureAwait(false);
        return outcomeResult;
    }

    private async Task<TerminalCloseOutcome> PumpUpstreamAsync(
        ITerminalChannel browser,
        TerminalSessionTelemetry telemetry,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var upstream = Volatile.Read(ref _upstream);
            if (upstream is null)
            {
                return new TerminalCloseOutcome(TerminalCloseReasons.UpstreamError, "1Panel 终端连接已中断。", true);
            }

            TerminalServerFrame frame;
            try
            {
                frame = await upstream.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
            }
            catch (Exception exception)
            {
                _logger.LogInformation(exception, "终端 {SessionId} 上游连接中断", _info.SessionId);
                _errorCode = "upstream_error";
                return new TerminalCloseOutcome(
                    TerminalCloseReasons.UpstreamError,
                    "与 1Panel 的终端连接已中断。",
                    true);
            }

            switch (frame.Kind)
            {
                case TerminalServerFrameKind.Output:
                    if (frame.Data is { Length: > 0 })
                    {
                        telemetry.AddOutput(frame.Data.Length);
                        if (!await TrySendAsync(browser, TerminalProtocol.EncodeOutput(frame.Data), token)
                            .ConfigureAwait(false))
                        {
                            return new TerminalCloseOutcome(TerminalCloseReasons.User, "浏览器连接已断开。", false);
                        }
                    }

                    break;

                case TerminalServerFrameKind.Closed:
                    if (frame.CloseCode == RevalidateCloseCode)
                    {
                        if (await TryRevalidateAsync(telemetry, token).ConfigureAwait(false))
                        {
                            continue;
                        }

                        _errorCode = "revalidate_failed";
                        return new TerminalCloseOutcome(
                            TerminalCloseReasons.RevalidateFailed,
                            "终端授权续租失败，请重新连接。",
                            true);
                    }

                    return MapUpstreamClose(frame);

                case TerminalServerFrameKind.Session:
                case TerminalServerFrameKind.Heartbeat:
                case TerminalServerFrameKind.AiNotice:
                case TerminalServerFrameKind.Unknown:
                default:
                    break;
            }
        }

        return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
    }

    private async Task<TerminalCloseOutcome> PumpBrowserAsync(
        ITerminalChannel browser,
        TerminalSessionTelemetry telemetry,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TerminalChannelMessage message;
            try
            {
                message = await browser.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
            }
            catch (Exception)
            {
                return new TerminalCloseOutcome(TerminalCloseReasons.User, "浏览器连接已断开。", false);
            }

            if (message.Kind != TerminalChannelMessageKind.Text)
            {
                return new TerminalCloseOutcome(TerminalCloseReasons.User, "终端已断开。", true);
            }

            if (!TerminalProtocol.TryParseBrowserFrame(message.Text!, out var frame))
            {
                continue;
            }

            MarkActivity();

            try
            {
                switch (frame.Kind)
                {
                    case TerminalBrowserFrameKind.Ping:
                        await TrySendAsync(browser, TerminalProtocol.EncodePong(frame.Timestamp), token).ConfigureAwait(false);
                        break;

                    case TerminalBrowserFrameKind.Resize:
                    {
                        var cols = Math.Clamp(frame.Cols, 20, 500);
                        var rows = Math.Clamp(frame.Rows, 5, 200);
                        var upstream = Volatile.Read(ref _upstream);
                        if (upstream is not null && !Volatile.Read(ref _revalidating))
                        {
                            await upstream.SendResizeAsync(cols, rows, token).ConfigureAwait(false);
                        }

                        break;
                    }

                    case TerminalBrowserFrameKind.Input:
                    {
                        var data = frame.Data ?? [];
                        if (data.Length > _limits.MaxFrameBytes)
                        {
                            _errorCode = "input_limit";
                            return new TerminalCloseOutcome(
                                TerminalCloseReasons.InputLimit,
                                "单个输入帧超过大小上限，已断开终端。",
                                false);
                        }

                        if (!_limiter.TryAcquire(data.Length))
                        {
                            _errorCode = "input_limit";
                            return new TerminalCloseOutcome(
                                TerminalCloseReasons.InputLimit,
                                "输入过于频繁，已断开终端。",
                                false);
                        }

                        telemetry.AddInput(data.Length);
                        if (Volatile.Read(ref _revalidating))
                        {
                            if (!BufferInput(data))
                            {
                                _errorCode = "input_limit";
                                return new TerminalCloseOutcome(
                                    TerminalCloseReasons.InputLimit,
                                    "终端续租期间输入过多，已断开连接。",
                                    false);
                            }

                            break;
                        }

                        var upstream = Volatile.Read(ref _upstream);
                        if (upstream is null)
                        {
                            break;
                        }

                        try
                        {
                            await upstream.SendInputAsync(data, token).ConfigureAwait(false);
                        }
                        catch (Exception) when (!token.IsCancellationRequested)
                        {
                            if (!BufferInput(data))
                            {
                                return new TerminalCloseOutcome(
                                    TerminalCloseReasons.UpstreamError,
                                    "与 1Panel 的终端连接已中断。",
                                    true);
                            }
                        }

                        break;
                    }

                    default:
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
            }
            catch (TerminalException exception)
            {
                _errorCode = exception.Code;
                return new TerminalCloseOutcome(TerminalCloseReasons.UpstreamError, exception.Message, true);
            }
            catch (Exception)
            {
                _errorCode = "upstream_error";
                return new TerminalCloseOutcome(
                    TerminalCloseReasons.UpstreamError,
                    "与 1Panel 的终端连接已中断。",
                    true);
            }
        }

        return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
    }

    private async Task<TerminalCloseOutcome> MonitorAsync(DateTimeOffset startedAt, CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_limits.IdleTimeout.TotalMilliseconds / 20, 50, 500));

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
            }

            var now = _time.GetUtcNow();
            if (now - startedAt >= _limits.MaxDuration)
            {
                return TerminalCloseOutcome.MaxDuration;
            }

            var lastActivity = new DateTimeOffset(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
            if (now - lastActivity >= _limits.IdleTimeout)
            {
                return TerminalCloseOutcome.Idle;
            }
        }

        return new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端连接已取消。", false);
    }

    private async Task<bool> TryRevalidateAsync(TerminalSessionTelemetry telemetry, CancellationToken token)
    {
        var current = Volatile.Read(ref _upstream);
        var sessionId = current?.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        Volatile.Write(ref _revalidating, true);
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(_limits.RevalidationDelayMs * attempt),
                            token)
                        .ConfigureAwait(false);

                    var connection = await _backend
                        .ConnectAsync(_context, sessionId, revalidate: true, token)
                        .ConfigureAwait(false);
                    var previous = Interlocked.Exchange(ref _upstream, connection);
                    if (previous is not null)
                    {
                        await previous.DisposeAsync().ConfigureAwait(false);
                    }

                    telemetry.IncrementRevalidate();
                    await FlushPendingInputAsync(token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return false;
                }
                catch (TerminalException exception)
                {
                    _logger.LogWarning(
                        "终端 {SessionId} 第 {Attempt} 次续租失败：{Code}",
                        _info.SessionId,
                        attempt,
                        exception.Code);
                }
            }

            return false;
        }
        finally
        {
            Volatile.Write(ref _revalidating, false);
        }
    }

    private async Task FlushPendingInputAsync(CancellationToken token)
    {
        byte[][] pending;
        lock (_inputGate)
        {
            if (_pendingInputs.Count == 0)
            {
                return;
            }

            pending = _pendingInputs.ToArray();
            _pendingInputs.Clear();
            _pendingInputBytes = 0;
        }

        var upstream = Volatile.Read(ref _upstream);
        if (upstream is null)
        {
            return;
        }

        foreach (var data in pending)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await upstream.SendInputAsync(data, token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                BufferInput(data);
                return;
            }
        }
    }

    private bool BufferInput(byte[] data)
    {
        lock (_inputGate)
        {
            if (_pendingInputBytes + data.Length > _limits.RevalidationBufferBytes)
            {
                return false;
            }

            _pendingInputs.Add(data);
            _pendingInputBytes += data.Length;
            return true;
        }
    }

    private async Task FinalizeAsync(ITerminalChannel browser, TerminalCloseOutcome outcome, bool sendClosed)
    {
        var upstream = Interlocked.Exchange(ref _upstream, null);
        if (upstream is not null)
        {
            try
            {
                await upstream.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "终端 {SessionId} 关闭上游时出错", _info.SessionId);
            }

            await upstream.DisposeAsync().ConfigureAwait(false);
        }

        if (sendClosed)
        {
            if (!string.IsNullOrEmpty(_errorCode))
            {
                await TrySendAsync(
                        browser,
                        TerminalProtocol.EncodeError(_errorCode, outcome.Message ?? "终端连接已结束。"),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await TrySendAsync(
                    browser,
                    TerminalProtocol.EncodeClosed(outcome.Reason, outcome.Message, outcome.CanReconnect),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        try
        {
            var statusCode = outcome.Reason is TerminalCloseReasons.UpstreamError
                or TerminalCloseReasons.RevalidateFailed
                or TerminalCloseReasons.InputLimit
                ? 1011
                : 1000;
            await browser
                .CloseAsync(statusCode, outcome.Message ?? "terminal closed", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "终端 {SessionId} 关闭浏览器通道时出错", _info.SessionId);
        }
    }

    private static TerminalCloseOutcome MapUpstreamClose(TerminalServerFrame frame)
    {
        return frame.CloseCode switch
        {
            1000 => new TerminalCloseOutcome(
                TerminalCloseReasons.UpstreamClosed,
                "1Panel 终端已结束（shell 退出或面板关闭了会话）。",
                true),
            SessionNotFoundCloseCode => new TerminalCloseOutcome(
                TerminalCloseReasons.UpstreamClosed,
                "1Panel 终端会话已过期或不存在，请重新连接。",
                true),
            AttachedElsewhereCloseCode => new TerminalCloseOutcome(
                TerminalCloseReasons.UpstreamClosed,
                "该终端会话已在其它连接中打开。",
                true),
            null => new TerminalCloseOutcome(
                TerminalCloseReasons.UpstreamClosed,
                "1Panel 关闭了终端连接。",
                true),
            _ => new TerminalCloseOutcome(
                TerminalCloseReasons.UpstreamClosed,
                $"1Panel 关闭了终端连接（code {frame.CloseCode.Value}）。",
                true)
        };
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);

    private static async Task<bool> TrySendAsync(
        ITerminalChannel channel,
        string payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendTextAsync(payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task IgnoreAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 结束阶段忽略各泵的异常，结束原因已由获胜任务给出。
            }
        }
    }
}
