using System.Collections.Concurrent;
using PanelFleet.Application.Abstractions;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Overview;

/// <summary>
/// 按节点维度的内存熔断器。
/// 只对传输类错误计数（网络/TLS/超时/协议/面板 5xx）；鉴权、参数、404 等配置类错误不熔断，
/// 以便用户修正配置后立即恢复。
/// </summary>
public sealed class NodeCircuitBreaker : INodeCircuitBreaker
{
    private readonly ConcurrentDictionary<NodeId, Entry> _entries = new();
    private readonly OverviewOptions _options;

    public NodeCircuitBreaker(OverviewOptions options)
    {
        _options = options;
    }

    public CircuitStatus GetStatus(NodeId nodeId, DateTimeOffset now)
    {
        var entry = _entries.GetOrAdd(nodeId, static _ => new Entry());
        lock (entry.Sync)
        {
            if (entry.OpenUntil is not { } openUntil)
            {
                return new CircuitStatus(CircuitState.Closed, entry.ConsecutiveFailures, null);
            }

            var state = now < openUntil ? CircuitState.Open : CircuitState.HalfOpen;
            return new CircuitStatus(state, entry.ConsecutiveFailures, openUntil);
        }
    }

    public bool TryEnter(NodeId nodeId, DateTimeOffset now, bool force)
    {
        var entry = _entries.GetOrAdd(nodeId, static _ => new Entry());
        lock (entry.Sync)
        {
            if (force)
            {
                return true;
            }

            if (entry.OpenUntil is not { } openUntil)
            {
                return true;
            }

            if (now < openUntil)
            {
                return false;
            }

            if (entry.ProbeInFlight)
            {
                return false;
            }

            entry.ProbeInFlight = true;
            return true;
        }
    }

    public void RecordSuccess(NodeId nodeId)
    {
        var entry = _entries.GetOrAdd(nodeId, static _ => new Entry());
        lock (entry.Sync)
        {
            entry.ConsecutiveFailures = 0;
            entry.OpenUntil = null;
            entry.ProbeInFlight = false;
        }
    }

    public void RecordFailure(NodeId nodeId, PanelErrorKind kind, DateTimeOffset now)
    {
        var entry = _entries.GetOrAdd(nodeId, static _ => new Entry());
        lock (entry.Sync)
        {
            if (!IsTripping(kind))
            {
                // 配置类错误不计数；若本次是半开探针，释放探针占用以便下次再探测。
                entry.ProbeInFlight = false;
                return;
            }

            entry.ConsecutiveFailures++;

            if (entry.OpenUntil is not null)
            {
                // 半开探针失败或强制重试失败：重新打开。
                entry.OpenUntil = now + _options.Circuit.BreakDuration;
                entry.ProbeInFlight = false;
                return;
            }

            if (entry.ConsecutiveFailures >= _options.Circuit.FailureThreshold)
            {
                entry.OpenUntil = now + _options.Circuit.BreakDuration;
                entry.ProbeInFlight = false;
            }
        }
    }

    private static bool IsTripping(PanelErrorKind kind) =>
        kind is PanelErrorKind.Network
            or PanelErrorKind.Tls
            or PanelErrorKind.Timeout
            or PanelErrorKind.Protocol
            or PanelErrorKind.PanelServerError;

    private sealed class Entry
    {
        public object Sync { get; } = new();

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? OpenUntil { get; set; }

        public bool ProbeInFlight { get; set; }
    }
}
