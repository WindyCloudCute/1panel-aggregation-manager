using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Overview;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Overview;

public sealed class OverviewSnapshotFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    private static readonly CircuitStatus Closed = new(CircuitState.Closed, 0, null);

    [Fact]
    public void NoAttempt_IsPending()
    {
        var node = TestNodes.Record();

        var snapshot = OverviewSnapshotFactory.Create(node, null, null, Closed, Now, StaleAfter);

        Assert.Equal(NodeDataState.Pending, snapshot.State);
        Assert.Equal(node.Health, snapshot.LiveHealth);
        Assert.Null(snapshot.Metrics);
    }

    [Fact]
    public void FreshMetrics_IsFreshAndOnline()
    {
        var node = TestNodes.Record();
        var data = TestNodes.Data(Now.AddSeconds(-10), TestNodes.Metrics(Now.AddSeconds(-10)));

        var snapshot = OverviewSnapshotFactory.Create(node, "prod", data, Closed, Now, StaleAfter);

        Assert.Equal(NodeDataState.Fresh, snapshot.State);
        Assert.Equal(NodeHealth.Online, snapshot.LiveHealth);
        Assert.False(snapshot.IsStale);
        Assert.Equal("prod", snapshot.GroupName);
    }

    [Fact]
    public void OldMetrics_MarkStale()
    {
        var node = TestNodes.Record();
        var fetched = Now.AddSeconds(-200);
        var data = TestNodes.Data(fetched, TestNodes.Metrics(fetched), fetchedAt: fetched);

        var snapshot = OverviewSnapshotFactory.Create(node, null, data, Closed, Now, StaleAfter);

        Assert.Equal(NodeDataState.Stale, snapshot.State);
        Assert.True(snapshot.IsStale);
    }

    [Fact]
    public void ErrorWithoutMetrics_IsFailed()
    {
        var node = TestNodes.Record();
        var error = new PanelError(PanelErrorKind.Network, "boom");
        var data = TestNodes.Data(Now, error: error);

        var snapshot = OverviewSnapshotFactory.Create(node, null, data, Closed, Now, StaleAfter);

        Assert.Equal(NodeDataState.Failed, snapshot.State);
        Assert.Equal(NodeHealth.Offline, snapshot.LiveHealth);
        Assert.Null(snapshot.Metrics);
    }

    [Fact]
    public void ErrorWithMetrics_KeepsCachedDataAndMarksStale()
    {
        var node = TestNodes.Record();
        var metrics = TestNodes.Metrics(Now.AddSeconds(-30));
        var error = new PanelError(PanelErrorKind.Timeout, "slow");
        var data = TestNodes.Data(Now, metrics: metrics, error: error, fetchedAt: Now.AddSeconds(-30));

        var snapshot = OverviewSnapshotFactory.Create(node, null, data, Closed, Now, StaleAfter);

        Assert.Equal(NodeDataState.Stale, snapshot.State);
        Assert.NotNull(snapshot.Metrics);
        Assert.Equal(NodeHealth.Offline, snapshot.LiveHealth);
    }

    [Fact]
    public void DisabledNode_IsDisabled_EvenWithCachedMetrics()
    {
        var node = TestNodes.Record(enabled: false, health: NodeHealth.Unknown);
        var data = TestNodes.Data(Now.AddSeconds(-10), TestNodes.Metrics(Now.AddSeconds(-10)));

        var snapshot = OverviewSnapshotFactory.Create(node, null, data, Closed, Now, StaleAfter);

        Assert.Equal(NodeDataState.Disabled, snapshot.State);
        Assert.Null(snapshot.Metrics);
        Assert.Equal(NodeHealth.Unknown, snapshot.LiveHealth);
    }

    [Fact]
    public void OpenCircuit_IsExposed()
    {
        var node = TestNodes.Record();
        var circuit = new CircuitStatus(CircuitState.Open, 3, Now.AddSeconds(20));

        var snapshot = OverviewSnapshotFactory.Create(node, null, null, circuit, Now, StaleAfter);

        Assert.True(snapshot.CircuitOpen);
        Assert.Equal(Now.AddSeconds(20), snapshot.CircuitOpenUntil);
        Assert.Equal(3, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public void Build_AggregatesOnlyNodesWithData_AndSortsEnabledFirst()
    {
        var online = TestNodes.Record(name: "a-online");
        var offline = TestNodes.Record(name: "b-offline");
        var disabled = TestNodes.Record(name: "c-disabled", enabled: false, health: NodeHealth.Unknown);

        var onlineSnapshot = OverviewSnapshotFactory.Create(
            online,
            null,
            TestNodes.Data(Now, TestNodes.Metrics(Now, cpu: 10)),
            Closed,
            Now,
            StaleAfter);
        var offlineSnapshot = OverviewSnapshotFactory.Create(
            offline,
            null,
            TestNodes.Data(Now, error: new PanelError(PanelErrorKind.Network, "boom")),
            Closed,
            Now,
            StaleAfter);
        var disabledSnapshot = OverviewSnapshotFactory.Create(
            disabled,
            null,
            null,
            Closed,
            Now,
            StaleAfter);

        var overview = OverviewSnapshotFactory.Build(
            [offlineSnapshot, disabledSnapshot, onlineSnapshot],
            Now,
            isRefreshing: true,
            lastRunElapsed: TimeSpan.FromMilliseconds(1234),
            generation: 7);

        Assert.Equal(["a-online", "b-offline", "c-disabled"], overview.Nodes.Select(node => node.Name));
        Assert.Equal(3, overview.Totals.NodeCount);
        Assert.Equal(2, overview.Totals.EnabledCount);
        Assert.Equal(1, overview.Totals.DisabledCount);
        Assert.Equal(1, overview.Totals.OnlineCount);
        Assert.Equal(1, overview.Totals.OfflineCount);
        Assert.Equal(1, overview.Totals.NodesWithData);
        Assert.Equal(2, overview.Totals.WebsiteCount);
        Assert.Equal(1, overview.Totals.AppCount);
        Assert.Equal(10, overview.Totals.CpuUsedPercentAvg);
        Assert.Equal(100, overview.Totals.MemoryUsedBytes);
        Assert.Equal(200, overview.Totals.MemoryTotalBytes);
        Assert.Equal(50, overview.Totals.DiskUsedPercent);
        Assert.True(overview.IsRefreshing);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), overview.LastRunElapsed);
        Assert.Equal(7, overview.Generation);
        Assert.NotNull(overview.DataUpdatedAt);
    }

    [Fact]
    public void Build_EmptyCluster_HasNoData()
    {
        var overview = OverviewSnapshotFactory.Build([], Now, false, null, 0);

        Assert.Empty(overview.Nodes);
        Assert.Null(overview.Totals.CpuUsedPercentAvg);
        Assert.Null(overview.Totals.DiskUsedPercent);
        Assert.Null(overview.DataUpdatedAt);
    }
}
