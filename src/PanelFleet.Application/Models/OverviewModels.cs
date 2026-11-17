using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Models;

/// <summary>单个磁盘分区的用量（来自 currentInfo.diskData）。</summary>
public sealed record DiskUsage(string Path, long? TotalBytes, long? UsedBytes, double? UsedPercent);

/// <summary>节点实时指标（来自 GET /api/v2/dashboard/base/all/all）。</summary>
public sealed record NodeMetrics(
    string? Hostname,
    string? OsDescription,
    string? KernelVersion,
    string? KernelArch,
    string? IpAddress,
    string? CpuModelName,
    int? CpuCores,
    int? WebsiteCount,
    int? AppCount,
    int? DatabaseCount,
    int? CronjobCount,
    double? CpuUsedPercent,
    double? MemoryUsedPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? MaxDiskUsedPercent,
    string? MaxDiskPath,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    IReadOnlyList<DiskUsage> Disks,
    double? Load1,
    double? Load5,
    double? Load15,
    long? UptimeSeconds,
    DateTimeOffset SampledAt);

/// <summary>总览卡片的数据状态。</summary>
public enum NodeDataState
{
    /// <summary>节点已禁用，不采集。</summary>
    Disabled = 0,

    /// <summary>尚未采集过。</summary>
    Pending = 1,

    /// <summary>有新鲜指标且最近一次采集成功。</summary>
    Fresh = 2,

    /// <summary>有指标，但已过期或最近一次采集失败。</summary>
    Stale = 3,

    /// <summary>从未采集成功，最近一次失败。</summary>
    Failed = 4
}

/// <summary>单节点总览快照（数据库静态信息 + 缓存动态数据 + 熔断状态）。</summary>
public sealed record NodeOverviewSnapshot(
    NodeId NodeId,
    string Name,
    string? GroupName,
    IReadOnlyList<string> Tags,
    Uri BaseUri,
    string? SecurityEntrance,
    string? PanelVersion,
    bool IsEnabled,
    NodeHealth Health,
    NodeHealth LiveHealth,
    NodeDataState State,
    NodeMetrics? Metrics,
    PanelError? Error,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? AttemptedAt,
    TimeSpan? LastLatency,
    DateTimeOffset? LastCheckedAt,
    bool IsStale,
    bool CircuitOpen,
    DateTimeOffset? CircuitOpenUntil,
    int ConsecutiveFailures);

/// <summary>集群汇总（只统计有数据的启用节点）。</summary>
public sealed record FleetTotals(
    int NodeCount,
    int EnabledCount,
    int DisabledCount,
    int OnlineCount,
    int DegradedCount,
    int OfflineCount,
    int UnknownCount,
    int NodesWithData,
    int StaleCount,
    int ErrorCount,
    int CircuitOpenCount,
    int WebsiteCount,
    int AppCount,
    int DatabaseCount,
    int CronjobCount,
    double? CpuUsedPercentAvg,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskUsedBytes,
    long DiskTotalBytes,
    double? DiskUsedPercent);

/// <summary>聚合总览快照。</summary>
public sealed record FleetOverview(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? DataUpdatedAt,
    IReadOnlyList<NodeOverviewSnapshot> Nodes,
    FleetTotals Totals,
    bool IsRefreshing,
    TimeSpan? LastRunElapsed,
    long Generation);

/// <summary>总览增量事件（由 broadcaster 推送给订阅者）。</summary>
public abstract record OverviewEvent
{
    public sealed record RunStarted(int NodeCount, DateTimeOffset StartedAt) : OverviewEvent;

    public sealed record NodeUpdated(NodeOverviewSnapshot Snapshot) : OverviewEvent;

    public sealed record RunCompleted(FleetOverview Overview, TimeSpan Elapsed, bool BudgetExceeded) : OverviewEvent;
}

/// <summary>缓存中的单节点动态数据（不含数据库静态字段）。</summary>
public sealed record NodeOverviewData(
    NodeMetrics? Metrics,
    PanelError? Error,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? AttemptedAt,
    TimeSpan LastLatency,
    bool LastAttemptSucceeded);

/// <summary>缓存快照（不可变拷贝）。</summary>
public sealed record OverviewCacheSnapshot(
    IReadOnlyDictionary<NodeId, NodeOverviewData> Nodes,
    bool IsRefreshing,
    DateTimeOffset? LastCompletedAt,
    TimeSpan? LastRunElapsed,
    long Generation);
