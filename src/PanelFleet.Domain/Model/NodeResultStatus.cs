namespace PanelFleet.Domain.Model;

/// <summary>节点级调用结果状态。</summary>
public enum NodeResultStatus
{
    /// <summary>本次调用成功。</summary>
    Ok = 0,

    /// <summary>本次失败，返回 TTL 内的最近快照。</summary>
    Stale = 1,

    /// <summary>失败且无可用快照。</summary>
    Failed = 2,

    /// <summary>该节点/版本不具备此能力。</summary>
    Unsupported = 3
}
