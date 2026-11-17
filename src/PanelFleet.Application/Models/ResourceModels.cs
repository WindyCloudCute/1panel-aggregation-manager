using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Models;

/// <summary>P4 五类只读资源。</summary>
public enum ResourceKind
{
    Websites = 0,
    Apps = 1,
    Containers = 2,
    Databases = 3,
    Cronjobs = 4
}

/// <summary>单节点单类资源的采集状态。</summary>
public enum ResourceNodeState
{
    Success = 0,
    Partial = 1,
    Failed = 2,
    Skipped = 3,
    Pending = 4
}

/// <summary>合并后的一行：节点 + 资源。</summary>
public sealed record ResourceRow(
    NodeId NodeId,
    string NodeName,
    ResourceKind Kind,
    string SourceId,
    string Name,
    string? Status,
    IReadOnlyDictionary<string, string?> Attributes);

/// <summary>单节点单类资源的采集结果（缓存单位）。</summary>
public sealed record NodeResourceResult(
    NodeId NodeId,
    string NodeName,
    ResourceKind Kind,
    ResourceNodeState State,
    IReadOnlyList<ResourceRow> Rows,
    int Total,
    bool Truncated,
    IReadOnlyList<string> Warnings,
    PanelError? Error,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? AttemptedAt,
    TimeSpan Elapsed);

/// <summary>列表页响应（合并/过滤/排序/分页后）。</summary>
public sealed record ResourcePage(
    ResourceKind Kind,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ResourceRow> Rows,
    int TotalMatched,
    int Page,
    int PageSize,
    int TotalPages,
    int NodesSucceeded,
    int NodesFailed,
    int NodesTotal,
    IReadOnlyList<NodeResourceResult> Nodes);

/// <summary>详情（按需加载）。</summary>
public sealed record ResourceDetail(
    NodeId NodeId,
    string NodeName,
    ResourceKind Kind,
    string SourceId,
    string Name,
    IReadOnlyDictionary<string, string?> Fields,
    DateTimeOffset FetchedAt,
    PanelError? Error);

/// <summary>查询参数。</summary>
public sealed record ResourceQuery(
    string? Filter,
    string? Sort,
    bool Descending,
    int Page,
    int PageSize,
    NodeId? NodeId);

/// <summary>查询模式：EnsureFresh 会触发采集，CacheOnly 只读缓存。</summary>
public enum ResourceQueryMode
{
    EnsureFresh = 0,
    CacheOnly = 1
}
