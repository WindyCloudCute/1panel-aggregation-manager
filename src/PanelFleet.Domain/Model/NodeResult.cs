namespace PanelFleet.Domain.Model;

/// <summary>聚合场景下单个节点的结果。</summary>
public sealed record NodeResult<T>(
    NodeId NodeId,
    NodeResultStatus Status,
    T? Data,
    PanelError? Error,
    TimeSpan Elapsed,
    DateTimeOffset? CachedAt = null);
