using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Cronjobs;

// P9 计划任务 DTO：全部宽松解析（字段缺失/类型变化不抛异常）。
// 注意：列表/详情 DTO 不映射 secret（备份压缩密码），避免敏感字段进入应用模型。

/// <summary>计划任务列表项（1Panel <c>cronjobs/search</c>）。</summary>
public sealed class CronjobInfoDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public long GroupID { get; set; }

    public bool SpecCustom { get; set; }

    public string? Spec { get; set; }

    public string? Executor { get; set; }

    public string? ScriptMode { get; set; }

    public string? Command { get; set; }

    public string? ContainerName { get; set; }

    public string? User { get; set; }

    public long ScriptID { get; set; }

    public string? AppID { get; set; }

    public string? Website { get; set; }

    public string? ExclusionRules { get; set; }

    public string? DBType { get; set; }

    public string? DBName { get; set; }

    public string? URL { get; set; }

    public bool IsDir { get; set; }

    public string? SourceDir { get; set; }

    public int RetainCopies { get; set; }

    public int RetryTimes { get; set; }

    public long Timeout { get; set; }

    public bool IgnoreErr { get; set; }

    public bool IsExecuting { get; set; }

    public string? Status { get; set; }

    public string? LastRecordStatus { get; set; }

    public string? LastRecordTime { get; set; }

    public string? Args { get; set; }

    public int AlertCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>执行记录项（1Panel <c>cronjobs/search/records</c>）。</summary>
public sealed class CronjobRecordDto
{
    public long Id { get; set; }

    public long CronjobID { get; set; }

    public string? TaskID { get; set; }

    public string? File { get; set; }

    public string? StartTime { get; set; }

    public string? Records { get; set; }

    public string? Status { get; set; }

    public string? Message { get; set; }

    public string? TargetPath { get; set; }

    public double Interval { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>列表查询（1Panel PageCronjob；orderBy/order 必填）。</summary>
public sealed record CronjobSearchRequest(
    int Page,
    int PageSize,
    string Info,
    IReadOnlyList<long> GroupIDs,
    string OrderBy,
    string Order);

/// <summary>按 id 操作（详情/立即执行/记录日志）。</summary>
public sealed record CronjobIdRequest(long Id);

/// <summary>删除（可批量）。</summary>
public sealed record CronjobDeleteRequest(
    IReadOnlyList<long> Ids,
    bool CleanData,
    bool CleanRemoteData);

/// <summary>启用/停用。</summary>
public sealed record CronjobStatusRequest(long Id, string Status);

/// <summary>周期预览。</summary>
public sealed record CronjobSpecRequest(string Spec);

/// <summary>记录查询（时间为 RFC3339）。</summary>
public sealed record CronjobRecordSearchRequest(
    int Page,
    int PageSize,
    long CronjobID,
    string StartTime,
    string EndTime,
    string Status);
