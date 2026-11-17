using System.Collections.Concurrent;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Resources;

/// <summary>按 (节点, 资源类型) 的采集闸门；已有采集在跑时后来者直接用缓存，避免重复打节点。</summary>
public sealed class ResourceGates
{
    private readonly ConcurrentDictionary<(NodeId NodeId, ResourceKind Kind), SemaphoreSlim> _gates = new();

    public bool TryEnter(NodeId nodeId, ResourceKind kind) =>
        _gates.GetOrAdd((nodeId, kind), static _ => new SemaphoreSlim(1, 1)).Wait(0);

    public void Exit(NodeId nodeId, ResourceKind kind)
    {
        if (_gates.TryGetValue((nodeId, kind), out var gate))
        {
            gate.Release();
        }
    }
}

/// <summary>记录资源类型的最近访问时间（后台刷新只刷新最近访问过的类型）。</summary>
public sealed class ResourceAccessTracker : IResourceAccessTracker
{
    private readonly ConcurrentDictionary<ResourceKind, DateTimeOffset> _lastAccess = new();
    private readonly ResourceOptions _options;

    public ResourceAccessTracker(ResourceOptions options)
    {
        _options = options;
    }

    public void MarkAccessed(ResourceKind kind) => _lastAccess[kind] = DateTimeOffset.UtcNow;

    public IReadOnlyList<ResourceKind> RecentlyAccessed(DateTimeOffset now) =>
        _lastAccess
            .Where(pair => now - pair.Value <= _options.AccessTtl)
            .Select(pair => pair.Key)
            .ToArray();
}
