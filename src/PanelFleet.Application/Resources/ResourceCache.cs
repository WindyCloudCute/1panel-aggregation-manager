using System.Collections.Concurrent;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Resources;

/// <summary>资源列表/详情缓存；列表写入按 AttemptedAt CAS，防止旧结果覆盖新结果。</summary>
public sealed class ResourceCache : IResourceCache
{
    private readonly ConcurrentDictionary<(NodeId NodeId, ResourceKind Kind), NodeResourceResult> _lists = new();
    private readonly ConcurrentDictionary<(NodeId NodeId, ResourceKind Kind, string SourceId), ResourceDetail> _details = new();

    public bool TryGet(NodeId nodeId, ResourceKind kind, out NodeResourceResult result) =>
        _lists.TryGetValue((nodeId, kind), out result!);

    public bool Apply(NodeResourceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        while (true)
        {
            if (!_lists.TryGetValue((result.NodeId, result.Kind), out var existing))
            {
                if (_lists.TryAdd((result.NodeId, result.Kind), result))
                {
                    return true;
                }

                continue;
            }

            if (existing.AttemptedAt is not null
                && (result.AttemptedAt is null || result.AttemptedAt <= existing.AttemptedAt))
            {
                return false;
            }

            if (_lists.TryUpdate((result.NodeId, result.Kind), result, existing))
            {
                return true;
            }
        }
    }

    public void Prune(IReadOnlySet<NodeId> validNodeIds)
    {
        ArgumentNullException.ThrowIfNull(validNodeIds);

        foreach (var key in _lists.Keys)
        {
            if (!validNodeIds.Contains(key.NodeId))
            {
                _lists.TryRemove(key, out _);
            }
        }

        foreach (var key in _details.Keys)
        {
            if (!validNodeIds.Contains(key.NodeId))
            {
                _details.TryRemove(key, out _);
            }
        }
    }

    public bool TryGetDetail(NodeId nodeId, ResourceKind kind, string sourceId, out ResourceDetail detail) =>
        _details.TryGetValue((nodeId, kind, sourceId), out detail!);

    public void SetDetail(ResourceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        _details[(detail.NodeId, detail.Kind, detail.SourceId)] = detail;
    }
}
