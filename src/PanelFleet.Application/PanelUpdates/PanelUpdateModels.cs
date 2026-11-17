using PanelFleet.Domain.Model;

namespace PanelFleet.Application.PanelUpdates;

/// <summary>单个节点的 1Panel 版本检查结果（只读，不触发升级）。</summary>
public sealed record PanelNodeVersionInfo(
    NodeId NodeId,
    string NodeName,
    bool NodeEnabled,
    bool Online,
    bool VersionReadable,
    string? CurrentVersion,
    string? AvailableVersion,
    string? ReleaseNote,
    bool UpdateAvailable,
    bool AlreadyLatest,
    long? DiskFreeBytes,
    string? DiskPath,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings,
    PanelError? Error,
    TimeSpan Duration)
{
    /// <summary>是否满足升级条件（在线、版本可读、有更新、无阻断项）。</summary>
    public bool CanUpgrade =>
        NodeEnabled && Online && VersionReadable && UpdateAvailable && BlockingReasons.Count == 0;
}

/// <summary>一次版本检查的聚合结果（只读）。</summary>
public sealed record PanelVersionCheckResult(
    DateTimeOffset CheckedAt,
    IReadOnlyList<PanelNodeVersionInfo> Nodes)
{
    /// <summary>可升级节点数。</summary>
    public int UpgradeableCount => Nodes.Count(node => node.CanUpgrade);

    /// <summary>已是最新版本节点数。</summary>
    public int UpToDateCount => Nodes.Count(node => node.AlreadyLatest);

    /// <summary>检查失败节点数（离线、版本不可读等）。</summary>
    public int FailedCount => Nodes.Count(node => !node.Online || !node.VersionReadable);

    /// <summary>全部可升级节点共同的目标版本；不一致或没有可升级节点时为 <c>null</c>。</summary>
    public string? CommonTargetVersion =>
        Nodes.Where(node => node.CanUpgrade)
            .Select(node => node.AvailableVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == 1
            ? Nodes.First(node => node.CanUpgrade).AvailableVersion
            : null;
}

/// <summary>升级预检的单个节点结论。</summary>
public sealed record PanelUpdatePrecheckItem(
    NodeId NodeId,
    string NodeName,
    string? CurrentVersion,
    string? TargetVersion,
    bool CanUpgrade,
    long? DiskFreeBytes,
    IReadOnlyList<string> BlockingReasons,
    PanelError? Error);

/// <summary>批量升级预检结果。</summary>
public sealed record PanelUpdatePrecheckResult(
    DateTimeOffset CheckedAt,
    IReadOnlyList<PanelUpdatePrecheckItem> Items)
{
    /// <summary>全部节点都通过预检时为 <c>true</c>。</summary>
    public bool CanUpgradeAll => Items.Count > 0 && Items.All(item => item.CanUpgrade);
}
