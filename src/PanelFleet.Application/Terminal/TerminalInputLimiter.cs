namespace PanelFleet.Application.Terminal;

/// <summary>
/// 终端输入限流（令牌桶）：同时限制输入帧频率与输入字节速率。
/// 超过限制的输入帧会被拒绝，由桥接层以 <c>input_limit</c> 关闭会话。
/// 线程安全，使用注入的 <see cref="TimeProvider"/> 便于单元测试。
/// </summary>
public sealed class TerminalInputLimiter
{
    private readonly TerminalLimits _limits;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private double _frameTokens;
    private double _byteTokens;
    private long _lastTicks;

    public TerminalInputLimiter(TerminalLimits limits, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);

        _limits = limits;
        _time = time;
        _frameTokens = limits.MaxInputFramesPerSecond;
        _byteTokens = limits.MaxInputBytesPerSecond;
        _lastTicks = time.GetUtcNow().UtcTicks;
    }

    /// <summary>尝试消耗一份配额（1 帧 + frameBytes 字节）。</summary>
    public bool TryAcquire(int frameBytes)
    {
        if (frameBytes < 0)
        {
            return false;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcTicks;
            var elapsedSeconds = Math.Max(0, now - _lastTicks) / (double)TimeSpan.TicksPerSecond;
            _lastTicks = now;
            _frameTokens = Math.Min(
                _limits.MaxInputFramesPerSecond,
                _frameTokens + (elapsedSeconds * _limits.MaxInputFramesPerSecond));
            _byteTokens = Math.Min(
                _limits.MaxInputBytesPerSecond,
                _byteTokens + (elapsedSeconds * _limits.MaxInputBytesPerSecond));

            if (_frameTokens < 1 || _byteTokens < frameBytes)
            {
                return false;
            }

            _frameTokens -= 1;
            _byteTokens -= frameBytes;
            return true;
        }
    }
}
