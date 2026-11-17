using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Containers;

/// <summary>容器状态字符串常量（与 1Panel v2.3.2 返回一致）。</summary>
public static class ContainerStates
{
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Paused = "paused";
    public const string Restarting = "restarting";
    public const string Created = "created";
    public const string Dead = "dead";
    public const string Removing = "removing";

    /// <summary>1Panel 容器搜索接口允许的状态过滤值。</summary>
    public static readonly IReadOnlyList<string> Filterable =
    [
        "all", Created, Running, Paused, Restarting, Removing, Exited, Dead
    ];

    /// <summary>把 1Panel 状态规格化为小写；未知值原样返回（小写）。</summary>
    public static string Normalize(string? state) => string.IsNullOrWhiteSpace(state) ? "unknown" : state.Trim().ToLowerInvariant();

    public static bool IsRunning(string? state) => string.Equals(Normalize(state), Running, StringComparison.Ordinal);

    public static bool IsStopped(string? state) =>
        Normalize(state) is Exited or Created or Dead;

    public static bool IsPaused(string? state) => string.Equals(Normalize(state), Paused, StringComparison.Ordinal);
}

/// <summary>单节点容器采集状态。</summary>
public enum ContainerNodeState
{
    Success = 0,
    Failed = 1
}

/// <summary>容器行上展示的实时资源占用（来自 GET /containers/list/stats）。</summary>
public sealed record ContainerRowStats(
    double CpuPercent,
    double MemoryPercent,
    long MemoryUsage,
    long MemoryLimit);

/// <summary>聚合列表中的一行：节点 + 容器。</summary>
public sealed record ContainerRow(
    Guid NodeId,
    string NodeName,
    string ContainerId,
    string Name,
    string? Image,
    string? State,
    string RunTime,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Networks,
    string Source,
    string? AppName,
    IReadOnlyList<string> Websites,
    bool IsPinned,
    string? Description,
    ContainerRowStats? Stats);

/// <summary>单节点容器列表结果。</summary>
public sealed record ContainerNodeResult(
    Guid NodeId,
    string NodeName,
    ContainerNodeState State,
    int Total,
    IReadOnlyList<ContainerRow> Rows,
    bool Truncated,
    PanelError? Error,
    TimeSpan Elapsed);

/// <summary>多节点聚合容器列表。</summary>
public sealed record ContainerListResult(
    IReadOnlyList<ContainerRow> Rows,
    int TotalMatched,
    int Page,
    int PageSize,
    int TotalPages,
    int NodesTotal,
    int NodesSucceeded,
    int NodesFailed,
    IReadOnlyList<ContainerNodeResult> Nodes,
    DateTimeOffset GeneratedAt);

/// <summary>容器统计视图（实时 CPU/内存/IO/网络 + 可选磁盘占用）。</summary>
public sealed record ContainerStatsView(
    Guid NodeId,
    string NodeName,
    string ContainerIdOrName,
    double CpuPercent,
    double MemoryUsageMebibytes,
    double MemoryLimitMebibytes,
    double MemoryPercent,
    double IoRead,
    double IoWrite,
    double NetworkRx,
    double NetworkTx,
    DateTimeOffset? ShotTime,
    long? SizeRw,
    long? SizeRootFs,
    PanelError? Error,
    PanelError? SizeError);

/// <summary>容器详情（inspect 字段展平 + 运行统计）。</summary>
public sealed record ContainerDetail(
    Guid NodeId,
    string NodeName,
    string Name,
    string? ContainerId,
    IReadOnlyDictionary<string, string?> Fields,
    ContainerStatsView? Stats,
    DateTimeOffset FetchedAt,
    PanelError? Error);

/// <summary>容器日志 tail 请求。</summary>
public sealed record ContainerLogRequest(
    string ContainerName,
    string Since = "all",
    int Tail = 200,
    bool Timestamp = false);

/// <summary>容器日志 tail 结果（只读快照，非流式）。</summary>
public sealed record ContainerLogView(
    Guid NodeId,
    string NodeName,
    string ContainerName,
    string Since,
    int Tail,
    bool Timestamp,
    IReadOnlyList<string> Lines,
    bool Truncated,
    DateTimeOffset FetchedAt,
    PanelError? Error);

/// <summary>容器列表/Compose 列表的查询参数。</summary>
public sealed record ContainerQuery(
    Guid? NodeId,
    string? Name,
    string State,
    string OrderBy,
    string Order,
    int Page,
    int PageSize);

/// <summary>Compose 项下的容器。</summary>
public sealed record ComposeContainerView(
    string ContainerId,
    string Name,
    string State,
    IReadOnlyList<string> Ports);

/// <summary>Compose 项目（同一 Compose 文件的一组容器）。</summary>
public sealed record ComposeProjectRow(
    Guid NodeId,
    string NodeName,
    string Name,
    string Path,
    string CreatedBy,
    int ContainerCount,
    int RunningCount,
    bool ComposeFileExists,
    bool IsPinned,
    IReadOnlyList<ComposeContainerView> Containers,
    string? ConfigFile = null,
    string? Workdir = null);

/// <summary>只读读取的 Compose 文件内容（用于编辑弹窗预填；永不进入审计或日志）。</summary>
public sealed record ComposeContentResult(string Path, string Content, bool Truncated);

/// <summary>单节点 Compose 列表结果。</summary>
public sealed record ComposeNodeResult(
    Guid NodeId,
    string NodeName,
    ContainerNodeState State,
    int Total,
    IReadOnlyList<ComposeProjectRow> Projects,
    bool Truncated,
    PanelError? Error,
    TimeSpan Elapsed);

/// <summary>多节点聚合 Compose 列表。</summary>
public sealed record ComposeListResult(
    IReadOnlyList<ComposeProjectRow> Projects,
    int TotalMatched,
    int NodesTotal,
    int NodesSucceeded,
    int NodesFailed,
    IReadOnlyList<ComposeNodeResult> Nodes,
    DateTimeOffset GeneratedAt);

/// <summary>节点不存在或已禁用（Web 层映射为 404）。</summary>
public sealed class ContainerNodeNotFoundException : Exception
{
    public ContainerNodeNotFoundException(Guid nodeId)
        : base($"节点不存在或已禁用（{nodeId}）。")
    {
    }
}

/// <summary>Compose 项目不存在（Web 层映射为 404）。</summary>
public sealed class ContainerComposeNotFoundException : Exception
{
    public ContainerComposeNotFoundException(Guid nodeId, string name)
        : base($"Compose 项目不存在：{name}（节点 {nodeId}）。")
    {
    }
}

/// <summary>无法安全读取或编辑该 Compose 文件（Web 层映射为 400）。</summary>
public sealed class ComposeContentUnsupportedException : Exception
{
    public ComposeContentUnsupportedException(string message)
        : base(message)
    {
    }
}
