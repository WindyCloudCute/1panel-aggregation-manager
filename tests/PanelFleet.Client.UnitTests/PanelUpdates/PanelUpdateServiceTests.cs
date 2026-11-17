using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.PanelUpdates;
using PanelFleet.Client.UnitTests.Operations;
using PanelFleet.Client.UnitTests.Overview;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.PanelUpdates;

public sealed class PanelUpdateServiceTests
{
    private sealed class Harness
    {
        public Harness()
        {
            Service = new PanelUpdateService(
                Nodes,
                new FakeSecretProtector(),
                Factory,
                Options,
                new TestTimeProvider(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero)),
                NullLogger<PanelUpdateService>.Instance);
        }

        public ScriptedPanelClientFactory Factory { get; } = new();

        public FakeNodeStore Nodes { get; } = new();

        public PanelUpdateOptions Options { get; } = new() { CheckTimeoutSeconds = 5 };

        public PanelUpdateService Service { get; }

        public NodeRecord AddNode(string name, bool enabled = true)
        {
            var node = PanelUpdateTestData.Node(name, Factory, enabled);
            Nodes.Nodes.Add(node);
            return node;
        }

        public PanelUpdateNodeScript Script(string name) => Factory.Script(name);
    }

    [Fact]
    public async Task CheckVersions_ReturnsCurrentAndAvailableVersion()
    {
        var harness = new Harness();
        harness.AddNode("node-a");

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.Equal("node-a", node.NodeName);
        Assert.True(node.NodeEnabled);
        Assert.True(node.Online);
        Assert.True(node.VersionReadable);
        Assert.Equal("v2.3.2", node.CurrentVersion);
        Assert.Equal("v2.5.1", node.AvailableVersion);
        Assert.True(node.UpdateAvailable);
        Assert.False(node.AlreadyLatest);
        Assert.True(node.CanUpgrade);
        Assert.Contains("更新内容", node.ReleaseNote!, StringComparison.Ordinal);
        Assert.Equal(10L * 1024 * 1024 * 1024, node.DiskFreeBytes);
        Assert.Empty(node.BlockingReasons);
        Assert.Equal(1, result.UpgradeableCount);
        Assert.Equal("v2.5.1", result.CommonTargetVersion);
    }

    [Fact]
    public async Task CheckVersions_AlreadyLatest_IsNotUpgradeable()
    {
        var harness = new Harness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.CurrentVersion = "v2.5.1";
        script.LatestVersion = "v2.5.1";

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.VersionReadable);
        Assert.True(node.AlreadyLatest);
        Assert.False(node.UpdateAvailable);
        Assert.False(node.CanUpgrade);
        Assert.Contains(node.BlockingReasons, reason => reason.Contains("已是最新", StringComparison.Ordinal));
        Assert.Equal(1, result.UpToDateCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task CheckVersions_AlreadyLatestWithEmptyUpgradeInfo_IsReadableAndUpToDate()
    {
        // 真实 1Panel 在已是最新时 upgrade 接口的 newVersion/latestVersion 可能都为空。
        var harness = new Harness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.CurrentVersion = "v2.5.1";
        script.NewVersion = string.Empty;
        script.LatestVersion = string.Empty;

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.VersionReadable);
        Assert.True(node.AlreadyLatest);
        Assert.False(node.UpdateAvailable);
        Assert.False(node.CanUpgrade);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, result.UpToDateCount);
    }

    [Fact]
    public async Task CheckVersions_OfflineNode_ReportsUnreachable()
    {
        var harness = new Harness();
        harness.AddNode("offline-node");
        harness.Script("offline-node").DashboardReachable = false;

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.False(node.Online);
        Assert.False(node.VersionReadable);
        Assert.False(node.CanUpgrade);
        Assert.Contains(node.BlockingReasons, reason => reason.Contains("节点不可达", StringComparison.Ordinal));
        Assert.NotNull(node.Error);
        Assert.Equal(PanelErrorKind.Network, node.Error!.Kind);
    }

    [Fact]
    public async Task CheckVersions_CurrentVersionUnreadable_BlocksUpgrade()
    {
        var harness = new Harness();
        harness.AddNode("node-a");
        harness.Script("node-a").CurrentVersionReadable = false;

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.False(node.VersionReadable);
        Assert.False(node.CanUpgrade);
        Assert.Contains(node.BlockingReasons, reason => reason.Contains("无法读取当前版本", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckVersions_UpgradeInfoUnreadable_BlocksUpgrade()
    {
        var harness = new Harness();
        harness.AddNode("node-a");
        harness.Script("node-a").UpgradeInfoReadable = false;

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.VersionReadable);
        Assert.False(node.AlreadyLatest);
        Assert.False(node.CanUpgrade);
        Assert.Contains(node.BlockingReasons, reason => reason.Contains("无法读取可用版本", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckVersions_DiskInsufficient_BlocksUpgrade()
    {
        var harness = new Harness();
        harness.AddNode("node-a");
        harness.Script("node-a").DiskFreeBytes = 100L * 1024 * 1024; // 100 MiB < 500 MiB

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.UpdateAvailable);
        Assert.False(node.CanUpgrade);
        Assert.Contains(node.BlockingReasons, reason => reason.Contains("磁盘可用空间不足", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckVersions_DisabledNode_DoesNotCallPanel()
    {
        var harness = new Harness();
        harness.AddNode("disabled-node", enabled: false);

        var result = await harness.Service.CheckVersionsAsync(null, default);

        var node = Assert.Single(result.Nodes);
        Assert.False(node.NodeEnabled);
        Assert.False(node.Online);
        Assert.False(node.CanUpgrade);
        Assert.Contains(node.BlockingReasons, reason => reason.Contains("节点已禁用", StringComparison.Ordinal));
        Assert.Equal(0, harness.Script("disabled-node").HealthCalls);
    }

    [Fact]
    public async Task CheckVersions_NodeIdFilter_PreservesRequestedOrder()
    {
        var harness = new Harness();
        var nodeA = harness.AddNode("node-a");
        var nodeB = harness.AddNode("node-b");

        var result = await harness.Service.CheckVersionsAsync([nodeB.Id, nodeA.Id], default);

        Assert.Equal(new[] { "node-b", "node-a" }, result.Nodes.Select(node => node.NodeName));
    }

    [Fact]
    public async Task Precheck_RejectsAlreadyLatestOfflineAndUnreadableNodes()
    {
        var harness = new Harness();
        var current = harness.AddNode("current-node");
        harness.Script("current-node").CurrentVersion = "v2.5.1";
        harness.Script("current-node").LatestVersion = "v2.5.1";

        var offline = harness.AddNode("offline-node");
        harness.Script("offline-node").DashboardReachable = false;

        var unreadable = harness.AddNode("unreadable-node");
        harness.Script("unreadable-node").CurrentVersionReadable = false;

        var precheck = await harness.Service.PrecheckAsync(
            [current.Id, offline.Id, unreadable.Id],
            default);

        Assert.Equal(3, precheck.Items.Count);
        Assert.All(precheck.Items, item => Assert.False(item.CanUpgrade));
        Assert.False(precheck.CanUpgradeAll);
        Assert.Contains(
            precheck.Items.Single(item => item.NodeName == "current-node").BlockingReasons,
            reason => reason.Contains("已是最新", StringComparison.Ordinal));
        Assert.Contains(
            precheck.Items.Single(item => item.NodeName == "offline-node").BlockingReasons,
            reason => reason.Contains("节点不可达", StringComparison.Ordinal));
        Assert.Contains(
            precheck.Items.Single(item => item.NodeName == "unreadable-node").BlockingReasons,
            reason => reason.Contains("无法读取当前版本", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Precheck_EligibleNode_ReportsCurrentAndTargetVersion()
    {
        var harness = new Harness();
        var node = harness.AddNode("node-a");

        var precheck = await harness.Service.PrecheckAsync([node.Id], default);

        var item = Assert.Single(precheck.Items);
        Assert.True(item.CanUpgrade);
        Assert.Equal("v2.3.2", item.CurrentVersion);
        Assert.Equal("v2.5.1", item.TargetVersion);
        Assert.Empty(item.BlockingReasons);
        Assert.True(precheck.CanUpgradeAll);
    }

    [Fact]
    public async Task Precheck_UnknownNode_ReportsMissingNode()
    {
        var harness = new Harness();

        var precheck = await harness.Service.PrecheckAsync([NodeId.New()], default);

        var item = Assert.Single(precheck.Items);
        Assert.False(item.CanUpgrade);
        Assert.Contains(item.BlockingReasons, reason => reason.Contains("节点不存在", StringComparison.Ordinal));
    }
}
