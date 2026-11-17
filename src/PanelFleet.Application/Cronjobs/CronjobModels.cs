using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Cronjobs;

/// <summary>1Panel 计划任务类型（与前端 helper.ts 一致）。</summary>
public static class CronjobTypes
{
    public const string Shell = "shell";
    public const string App = "app";
    public const string Website = "website";
    public const string Database = "database";
    public const string Directory = "directory";
    public const string Log = "log";
    public const string Curl = "curl";
    public const string CutWebsiteLog = "cutWebsiteLog";
    public const string Clean = "clean";
    public const string Snapshot = "snapshot";
    public const string Ntp = "ntp";
    public const string SyncIpGroup = "syncIpGroup";
    public const string CleanLog = "cleanLog";

    /// <summary>全部类型及中文名（列表/筛选/徽标）。</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        (Shell, "Shell 脚本"),
        (App, "备份应用"),
        (Website, "备份网站"),
        (Database, "备份数据库"),
        (Directory, "备份目录"),
        (Log, "系统日志"),
        (Curl, "URL 访问"),
        (CutWebsiteLog, "网站日志切割"),
        (Clean, "磁盘清理"),
        (Snapshot, "系统快照"),
        (Ntp, "时间同步"),
        (SyncIpGroup, "同步 IP 组"),
        (CleanLog, "日志清理")
    ];

    /// <summary>本轮创建表单支持的类型（其余类型可管理但不提供新建表单）。</summary>
    public static readonly IReadOnlyList<string> CreateSupported =
        [Shell, Curl, Directory, App, Website, Database];

    public static bool IsKnown(string? type) =>
        All.Any(item => string.Equals(item.Value, type, StringComparison.OrdinalIgnoreCase));

    public static bool IsCreateSupported(string? type) =>
        CreateSupported.Any(value => string.Equals(value, type, StringComparison.OrdinalIgnoreCase));

    public static string Label(string? type) =>
        All.FirstOrDefault(item => string.Equals(item.Value, type, StringComparison.OrdinalIgnoreCase)).Label ?? (type ?? "未知类型");
}

/// <summary>1Panel 启用/停用状态。</summary>
public static class CronjobStatus
{
    public const string Enable = "Enable";
    public const string Disable = "Disable";

    public static bool IsEnable(string? status) => string.Equals(status, Enable, StringComparison.OrdinalIgnoreCase);

    public static string Label(string? status) => status switch
    {
        _ when IsEnable(status) => "启用",
        _ when string.Equals(status, Disable, StringComparison.OrdinalIgnoreCase) => "停用",
        _ => status ?? "未知"
    };
}

/// <summary>执行记录状态中文名。</summary>
public static class CronjobRecordStatus
{
    public static string Label(string? status) => status switch
    {
        "Success" => "成功",
        "Failed" => "失败",
        "Waiting" => "等待中",
        "Running" => "执行中",
        "Unexecuted" => "未执行",
        "Canceled" => "已取消",
        _ => status ?? "未知"
    };
}

/// <summary>单节点任务采集状态。</summary>
public enum CronjobNodeState
{
    Success = 0,
    Failed = 1
}

/// <summary>聚合列表中的一行：节点 + 计划任务。</summary>
public sealed record CronjobRow(
    Guid NodeId,
    string NodeName,
    long Id,
    string Name,
    string Type,
    string TypeText,
    long GroupId,
    string GroupName,
    string? Spec,
    string SpecText,
    bool SpecCustom,
    string? Status,
    string? LastRecordStatus,
    string? LastRecordTime,
    bool IsExecuting,
    string? SourceText,
    int RetainCopies,
    int RetryTimes,
    long Timeout,
    bool IgnoreErr);

/// <summary>单节点任务列表结果。</summary>
public sealed record CronjobNodeResult(
    Guid NodeId,
    string NodeName,
    CronjobNodeState State,
    int Total,
    IReadOnlyList<CronjobRow> Rows,
    bool Truncated,
    PanelError? Error,
    TimeSpan Elapsed);

/// <summary>计划任务分组（跨节点聚合：同一分组名/id 合并）。</summary>
public sealed record CronjobGroupView(long Id, string Name, bool IsDefault, IReadOnlyList<Guid> NodeIds);

/// <summary>多节点聚合任务列表。</summary>
public sealed record CronjobListResult(
    IReadOnlyList<CronjobRow> Rows,
    int TotalMatched,
    int Page,
    int PageSize,
    int TotalPages,
    int NodesTotal,
    int NodesSucceeded,
    int NodesFailed,
    IReadOnlyList<CronjobNodeResult> Nodes,
    DateTimeOffset GeneratedAt);

/// <summary>任务列表查询参数。</summary>
public sealed record CronjobQuery(
    Guid? NodeId,
    IReadOnlyList<long> GroupIds,
    string? Info,
    string? Status,
    string OrderBy,
    string Order,
    int Page,
    int PageSize);

/// <summary>任务可编辑模型（详情返回给前端；不含 secret）。</summary>
public sealed record CronjobFormModel(
    long Id,
    string Name,
    string Type,
    long GroupID,
    bool SpecCustom,
    string Spec,
    string? Executor,
    string? ScriptMode,
    string? Script,
    string? Command,
    string? ContainerName,
    string? User,
    long ScriptID,
    string? AppID,
    string? Website,
    string? ExclusionRules,
    string? DBType,
    string? DBName,
    string? URL,
    bool IsDir,
    string? SourceDir,
    int RetainCopies,
    int RetryTimes,
    long Timeout,
    bool IgnoreErr,
    string? Args);

/// <summary>任务详情：只读字段 + 可编辑模型 + 敏感字段标记。</summary>
public sealed record CronjobDetail(
    Guid NodeId,
    string NodeName,
    long Id,
    string Name,
    string Type,
    IReadOnlyDictionary<string, string?> Fields,
    CronjobFormModel? Editable,
    bool SecretSet,
    DateTimeOffset FetchedAt,
    PanelError? Error);

/// <summary>执行记录行。</summary>
public sealed record CronjobRecordRow(
    long Id,
    string? Status,
    string StatusText,
    string? StartTime,
    double IntervalSeconds,
    string? TaskId,
    string? Message,
    string? TargetPath);

/// <summary>执行记录列表结果。</summary>
public sealed record CronjobRecordsResult(
    Guid NodeId,
    string NodeName,
    long CronjobId,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<CronjobRecordRow> Rows,
    DateTimeOffset FetchedAt,
    PanelError? Error);

/// <summary>执行记录日志（纯文本，超出上限截断）。</summary>
public sealed record CronjobRecordLog(
    Guid NodeId,
    long RecordId,
    string Content,
    bool Truncated,
    DateTimeOffset FetchedAt,
    PanelError? Error);

/// <summary>周期下次执行时间预览。</summary>
public sealed record CronjobSpecPreview(
    IReadOnlyList<string> NextTimes,
    DateTimeOffset FetchedAt,
    PanelError? Error);

/// <summary>节点不存在或已禁用（Web 层映射为 404）。</summary>
public sealed class CronjobNodeNotFoundException : Exception
{
    public CronjobNodeNotFoundException(Guid nodeId)
        : base($"节点不存在或已禁用（{nodeId}）。")
    {
    }
}

/// <summary>计划任务不存在（Web 层映射为 404）。</summary>
public sealed class CronjobNotFoundException : Exception
{
    public CronjobNotFoundException(Guid nodeId, long id)
        : base($"计划任务不存在（节点 {nodeId}，任务 #{id}）。")
    {
    }
}
