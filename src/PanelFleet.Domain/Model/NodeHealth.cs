namespace PanelFleet.Domain.Model;

/// <summary>节点健康状态。</summary>
public enum NodeHealth
{
    Unknown = 0,
    Online = 1,
    Degraded = 2,
    Offline = 3
}
