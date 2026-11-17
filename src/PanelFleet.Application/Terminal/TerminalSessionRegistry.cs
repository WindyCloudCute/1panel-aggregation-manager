namespace PanelFleet.Application.Terminal;

/// <summary>
/// 进程内活跃终端会话注册表（singleton）：并发上限与快照。
/// 终端是有状态长连接，多实例部署需粘性会话（见设计文档遗留项）。
/// </summary>
public sealed class TerminalSessionRegistry
{
    private readonly Dictionary<string, TerminalSessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TerminalOptions _options;

    public TerminalSessionRegistry(TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>当前活跃会话数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public int Limit => _options.MaxConcurrentSessions;

    /// <summary>注册会话；达到并发上限时返回 false。</summary>
    public bool TryRegister(TerminalSessionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        lock (_gate)
        {
            if (_sessions.Count >= _options.MaxConcurrentSessions)
            {
                return false;
            }

            _sessions[info.SessionId] = info;
            return true;
        }
    }

    /// <summary>更新会话状态（快照展示用）。</summary>
    public void UpdateState(string sessionId, TerminalSessionState state)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var info))
            {
                _sessions[sessionId] = info with { State = state };
            }
        }
    }

    /// <summary>注销会话（幂等）。</summary>
    public void Unregister(string sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <summary>当前会话快照（按开始时间倒序）。</summary>
    public IReadOnlyList<TerminalSessionInfo> Snapshot()
    {
        lock (_gate)
        {
            return _sessions.Values
                .OrderByDescending(info => info.StartedAt)
                .ToArray();
        }
    }
}
