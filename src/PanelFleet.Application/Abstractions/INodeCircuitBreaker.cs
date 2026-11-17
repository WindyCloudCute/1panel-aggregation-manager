using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

public enum CircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2
}

public sealed record CircuitStatus(CircuitState State, int ConsecutiveFailures, DateTimeOffset? OpenUntil);

/// <summary>按节点维度的内存熔断器。</summary>
public interface INodeCircuitBreaker
{
    CircuitStatus GetStatus(NodeId nodeId, DateTimeOffset now);

    /// <summary>判断本轮是否允许调用该节点；false 表示跳过（熔断打开或半开探针已被占用）。</summary>
    bool TryEnter(NodeId nodeId, DateTimeOffset now, bool force);

    void RecordSuccess(NodeId nodeId);

    void RecordFailure(NodeId nodeId, PanelErrorKind kind, DateTimeOffset now);
}
