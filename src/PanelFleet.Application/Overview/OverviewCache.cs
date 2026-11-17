using System.Collections.Concurrent;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Overview;

/// <summary>进程内总览快照缓存；写入按 AttemptedAt 做时序保护（CAS）。</summary>
public sealed class OverviewCache : IOverviewCache
{
    private readonly ConcurrentDictionary<NodeId, NodeOverviewData> _nodes = new();
    private readonly object _stateSync = new();

    private int _refreshing;
    private long _generation;
    private DateTimeOffset? _lastCompletedAt;
    private TimeSpan? _lastRunElapsed;

    public OverviewCacheSnapshot GetSnapshot()
    {
        var nodes = new Dictionary<NodeId, NodeOverviewData>(_nodes);
        lock (_stateSync)
        {
            return new OverviewCacheSnapshot(
                nodes,
                Volatile.Read(ref _refreshing) == 1,
                _lastCompletedAt,
                _lastRunElapsed,
                Interlocked.Read(ref _generation));
        }
    }

    public bool ApplyNode(NodeId nodeId, NodeOverviewData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        while (true)
        {
            if (!_nodes.TryGetValue(nodeId, out var existing))
            {
                if (_nodes.TryAdd(nodeId, data))
                {
                    Interlocked.Increment(ref _generation);
                    return true;
                }

                continue;
            }

            if (existing.AttemptedAt is not null
                && (data.AttemptedAt is null || data.AttemptedAt <= existing.AttemptedAt))
            {
                return false;
            }

            if (_nodes.TryUpdate(nodeId, data, existing))
            {
                Interlocked.Increment(ref _generation);
                return true;
            }
        }
    }

    public void Prune(IReadOnlySet<NodeId> validNodeIds)
    {
        ArgumentNullException.ThrowIfNull(validNodeIds);

        foreach (var nodeId in _nodes.Keys)
        {
            if (!validNodeIds.Contains(nodeId))
            {
                _nodes.TryRemove(nodeId, out _);
            }
        }
    }

    public void SetRefreshing(bool refreshing)
    {
        Volatile.Write(ref _refreshing, refreshing ? 1 : 0);
        Interlocked.Increment(ref _generation);
    }

    public void MarkRunCompleted(TimeSpan elapsed)
    {
        lock (_stateSync)
        {
            _lastCompletedAt = DateTimeOffset.UtcNow;
            _lastRunElapsed = elapsed;
        }

        Interlocked.Increment(ref _generation);
    }
}
