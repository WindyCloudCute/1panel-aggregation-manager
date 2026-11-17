using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

/// <summary>P4 资源查询/刷新用例。</summary>
public interface IResourceQueryService
{
    Task<ResourcePage> QueryAsync(
        ResourceKind kind,
        ResourceQuery query,
        ResourceQueryMode mode,
        CancellationToken cancellationToken);

    Task<ResourcePage> RefreshAsync(ResourceKind kind, ResourceQuery query, CancellationToken cancellationToken);

    Task<ResourcePage> RefreshNodeAsync(
        ResourceKind kind,
        NodeId nodeId,
        ResourceQuery query,
        CancellationToken cancellationToken);

    Task<ResourceDetail> GetDetailAsync(
        ResourceKind kind,
        NodeId nodeId,
        string sourceId,
        CancellationToken cancellationToken);
}

/// <summary>记录哪些资源类型最近被访问过（后台定时刷新只刷新这些类型）。</summary>
public interface IResourceAccessTracker
{
    void MarkAccessed(ResourceKind kind);

    IReadOnlyList<ResourceKind> RecentlyAccessed(DateTimeOffset now);
}
