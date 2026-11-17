using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Nodes;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Overview;

/// <summary>把数据库节点 + 缓存动态数据 + 熔断状态组装为快照与汇总（纯函数）。</summary>
public static class OverviewSnapshotFactory
{
    public static NodeOverviewSnapshot Create(
        NodeRecord node,
        string? groupName,
        NodeOverviewData? data,
        CircuitStatus circuit,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(node);

        var metrics = data?.Metrics;
        var error = data?.Error;
        var fetchedAt = data?.FetchedAt;
        var attemptedAt = data?.AttemptedAt;

        var state = DetermineState(node.IsEnabled, metrics, error, fetchedAt, attemptedAt, now, staleAfter);
        var isStale = state == NodeDataState.Stale;
        var liveHealth = DetermineLiveHealth(node, state, error);

        return new NodeOverviewSnapshot(
            NodeId: node.Id,
            Name: node.Name,
            GroupName: groupName,
            Tags: node.Tags,
            BaseUri: node.BaseUrl,
            SecurityEntrance: node.SecurityEntrance,
            PanelVersion: node.PanelVersion,
            IsEnabled: node.IsEnabled,
            Health: node.Health,
            LiveHealth: liveHealth,
            State: state,
            Metrics: state == NodeDataState.Disabled ? null : metrics,
            Error: error,
            FetchedAt: fetchedAt,
            AttemptedAt: attemptedAt,
            LastLatency: data?.LastLatency,
            LastCheckedAt: node.LastCheckedAt,
            IsStale: isStale,
            CircuitOpen: node.IsEnabled && circuit.State != CircuitState.Closed,
            CircuitOpenUntil: circuit.OpenUntil,
            ConsecutiveFailures: circuit.ConsecutiveFailures);
    }

    public static FleetOverview Build(
        IReadOnlyList<NodeOverviewSnapshot> nodes,
        DateTimeOffset now,
        bool isRefreshing,
        TimeSpan? lastRunElapsed,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var ordered = nodes
            .OrderBy(node => node.IsEnabled ? 0 : 1)
            .ThenBy(node => node.GroupName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var enabled = ordered.Where(node => node.IsEnabled).ToArray();
        var withData = ordered.Where(node => node.Metrics is not null).ToArray();

        var cpuValues = withData
            .Select(node => node.Metrics!.CpuUsedPercent)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();

        var memoryUsed = withData.Sum(node => node.Metrics!.MemoryUsedBytes ?? 0);
        var memoryTotal = withData.Sum(node => node.Metrics!.MemoryTotalBytes ?? 0);
        var diskUsed = withData.Sum(node => node.Metrics!.DiskUsedBytes ?? 0);
        var diskTotal = withData.Sum(node => node.Metrics!.DiskTotalBytes ?? 0);

        var totals = new FleetTotals(
            NodeCount: ordered.Length,
            EnabledCount: enabled.Length,
            DisabledCount: ordered.Length - enabled.Length,
            OnlineCount: enabled.Count(node => node.LiveHealth == NodeHealth.Online),
            DegradedCount: enabled.Count(node => node.LiveHealth == NodeHealth.Degraded),
            OfflineCount: enabled.Count(node => node.LiveHealth == NodeHealth.Offline),
            UnknownCount: enabled.Count(node => node.LiveHealth == NodeHealth.Unknown),
            NodesWithData: withData.Length,
            StaleCount: ordered.Count(node => node.IsStale),
            ErrorCount: ordered.Count(node => node.Error is not null),
            CircuitOpenCount: ordered.Count(node => node.CircuitOpen),
            WebsiteCount: withData.Sum(node => node.Metrics!.WebsiteCount ?? 0),
            AppCount: withData.Sum(node => node.Metrics!.AppCount ?? 0),
            DatabaseCount: withData.Sum(node => node.Metrics!.DatabaseCount ?? 0),
            CronjobCount: withData.Sum(node => node.Metrics!.CronjobCount ?? 0),
            CpuUsedPercentAvg: cpuValues.Length == 0 ? null : Math.Round(cpuValues.Average(), 1),
            MemoryUsedBytes: memoryUsed,
            MemoryTotalBytes: memoryTotal,
            DiskUsedBytes: diskUsed,
            DiskTotalBytes: diskTotal,
            DiskUsedPercent: diskTotal > 0 ? Math.Round(diskUsed * 100d / diskTotal, 1) : null);

        var dataUpdatedAt = withData
            .Where(node => node.FetchedAt is not null)
            .Select(node => node.FetchedAt!.Value)
            .DefaultIfEmpty()
            .Max();
        DateTimeOffset? latest = withData.Any(node => node.FetchedAt is not null) ? dataUpdatedAt : null;

        return new FleetOverview(now, latest, ordered, totals, isRefreshing, lastRunElapsed, generation);
    }

    private static NodeDataState DetermineState(
        bool isEnabled,
        NodeMetrics? metrics,
        PanelError? error,
        DateTimeOffset? fetchedAt,
        DateTimeOffset? attemptedAt,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        if (!isEnabled)
        {
            return NodeDataState.Disabled;
        }

        if (attemptedAt is null)
        {
            return NodeDataState.Pending;
        }

        if (error is not null)
        {
            return metrics is null ? NodeDataState.Failed : NodeDataState.Stale;
        }

        if (metrics is null)
        {
            return NodeDataState.Pending;
        }

        return fetchedAt is null || now - fetchedAt.Value > staleAfter
            ? NodeDataState.Stale
            : NodeDataState.Fresh;
    }

    private static NodeHealth DetermineLiveHealth(NodeRecord node, NodeDataState state, PanelError? error)
    {
        if (!node.IsEnabled || state == NodeDataState.Pending)
        {
            return node.Health;
        }

        if (error is not null)
        {
            return NodeHealthEvaluator.Evaluate(error).Status;
        }

        return NodeHealth.Online;
    }
}
