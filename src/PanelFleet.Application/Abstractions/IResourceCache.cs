using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

/// <summary>P4 资源列表/详情缓存（单例，线程安全）。</summary>
public interface IResourceCache
{
    bool TryGet(NodeId nodeId, ResourceKind kind, out NodeResourceResult result);

    /// <summary>AttemptedAt 时序保护：旧结果不覆盖新结果。</summary>
    bool Apply(NodeResourceResult result);

    void Prune(IReadOnlySet<NodeId> validNodeIds);

    bool TryGetDetail(NodeId nodeId, ResourceKind kind, string sourceId, out ResourceDetail detail);

    void SetDetail(ResourceDetail detail);
}
