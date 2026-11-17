using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Containers;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Containers;

public sealed class ContainerServiceTests
{
    [Fact]
    public async Task ListAsync_MergesNodes_WithStatsAndSource()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory()
            .Map("containers/search", ContainerTestPayloads.ContainerListJson)
            .Map("containers/list/stats", ContainerTestPayloads.ListStatsJson);
        var service = CreateService([node], factory);

        var result = await service.ListAsync(DefaultQuery(), CancellationToken.None);

        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.NodesSucceeded);
        Assert.Equal(0, result.NodesFailed);

        var web = result.Rows.Single(row => row.Name == "web");
        Assert.Equal("running", web.State);
        Assert.Equal("应用", web.Source);
        Assert.NotNull(web.Stats);
        Assert.Equal(1.5, web.Stats!.CpuPercent, 3);
        Assert.Equal("Nginx", web.AppName);
        Assert.Contains("8080", web.Ports[0], StringComparison.Ordinal);

        var worker = result.Rows.Single(row => row.Name == "worker");
        Assert.Equal("手动", worker.Source);
        Assert.Null(worker.AppName);

        var searchCall = factory.Calls.First(call => call.Path.Contains("containers/search", StringComparison.Ordinal));
        Assert.Contains("\"state\":\"all\"", searchCall.Body, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"name\"", searchCall.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_NodeFailure_IsIsolated()
    {
        var good = ContainerTestData.Node("node-a");
        var bad = ContainerTestData.Node("bad-node");
        var factory = new FakePanelClientFactory()
            .Map("containers/search", ContainerTestPayloads.ContainerListJson)
            .Map("containers/list/stats", "[]")
            .Fail("bad-node", new PanelError(PanelErrorKind.Unauthorized, "bad key"));
        var service = CreateService([good, bad], factory);

        var result = await service.ListAsync(DefaultQuery(), CancellationToken.None);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.NodesSucceeded);
        Assert.Equal(1, result.NodesFailed);
        var failed = result.Nodes.Single(node => node.NodeName == "bad-node");
        Assert.Equal(ContainerNodeState.Failed, failed.State);
        Assert.Equal(PanelErrorKind.Unauthorized, failed.Error?.Kind);
    }

    [Fact]
    public async Task ListAsync_NodeFilter_OnlyQueriesSelectedNode()
    {
        var nodeA = ContainerTestData.Node("node-a");
        var nodeB = ContainerTestData.Node("node-b");
        var factory = new FakePanelClientFactory()
            .Map("containers/search", ContainerTestPayloads.ContainerListJson)
            .Map("containers/list/stats", "[]");
        var service = CreateService([nodeA, nodeB], factory);

        var result = await service.ListAsync(
            new ContainerQuery(nodeA.Id.Value, null, "all", "name", "ascending", 1, 50),
            CancellationToken.None);

        Assert.Equal(1, result.NodesTotal);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("node-a", row.NodeName));
        Assert.DoesNotContain(factory.Calls, call => call.NodeName == "node-b");
    }

    [Fact]
    public async Task GetDetailAsync_MasksSensitiveEnvironmentValues()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory()
            .Map("containers/inspect", ContainerTestPayloads.InspectJson)
            .Map("containers/info", ContainerTestPayloads.InfoJson)
            .Map("containers/stats/", ContainerTestPayloads.RuntimeStatsJson)
            .Map("containers/item/stats", ContainerTestPayloads.ItemStatsJson);
        var service = CreateService([node], factory);

        var detail = await service.GetDetailAsync(node.Id.Value, "web", CancellationToken.None);

        Assert.Null(detail.Error);
        Assert.Equal("web", detail.Name);
        Assert.Equal("running", detail.Fields["state"]);
        Assert.Equal("bridge: 172.17.0.2", detail.Fields["networks"]);
        Assert.Contains("MYSQL_ROOT_PASSWORD=***", detail.Fields["env"], StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", detail.Fields["env"], StringComparison.Ordinal);
        Assert.Equal("webstack", detail.Fields["composeProject"]);
        Assert.NotNull(detail.Stats);
        Assert.Equal(512, detail.Stats!.SizeRw);
        Assert.Equal(1.5, detail.Stats.CpuPercent, 3);
    }

    [Fact]
    public async Task GetLogsAsync_ReturnsLinesFromLogClient()
    {
        var node = ContainerTestData.Node("node-a");
        var logFactory = new FakeContainerLogClientFactory { Lines = ["[INFO] started", "[INFO] ready"] };
        var service = CreateService([node], new FakePanelClientFactory(), logFactory);

        var log = await service.GetLogsAsync(
            node.Id.Value,
            new ContainerLogRequest("web", "1h", 100, Timestamp: true),
            CancellationToken.None);

        Assert.Null(log.Error);
        Assert.Equal(2, log.Lines.Count);
        Assert.Equal("1h", logFactory.LastQuery?.Since);
        Assert.Equal(100, logFactory.LastQuery?.Tail);
        Assert.True(logFactory.LastQuery!.Timestamp);
    }

    [Fact]
    public async Task ListComposeAsync_DropsComposeEnvironmentSecrets()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory().Map("containers/compose/search", ContainerTestPayloads.ComposeSearchJson);
        var service = CreateService([node], factory);

        var result = await service.ListComposeAsync(DefaultQuery(), CancellationToken.None);

        var project = Assert.Single(result.Projects);
        Assert.Equal("webstack", project.Name);
        Assert.Equal(1, project.RunningCount);
        Assert.Single(project.Containers);

        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("super-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"env\"", serialized, StringComparison.Ordinal);
    }

    private static ContainerQuery DefaultQuery() => new(null, null, "all", "name", "ascending", 1, 50);

    private static ContainerService CreateService(
        IReadOnlyList<NodeRecord> nodes,
        FakePanelClientFactory factory,
        FakeContainerLogClientFactory? logFactory = null) =>
        new(
            new FakeNodeStore(nodes),
            new FakeSecretProtector(),
            factory,
            logFactory ?? new FakeContainerLogClientFactory(),
            new ContainerOptions { MaxConcurrency = 2, PerNodePageSize = 50, VerifyTimeoutSeconds = 0 },
            TimeProvider.System,
            NullLogger<ContainerService>.Instance);
}
