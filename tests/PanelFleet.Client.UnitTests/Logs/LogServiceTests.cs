using PanelFleet.Application.Logs;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Logs;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Logs;

public sealed class LogServiceTests
{
    [Fact]
    public async Task PanelOperation_MapsFieldsAndFiltersByKeyword()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.OperationItems.Add(LogTestData.Operation("更新系统 => [v2.5.1]"));
        script.OperationItems.Add(LogTestData.Operation("清空日志 => login", source: "logs", status: "Failed"));
        script.OperationTotal = 2;

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.Panel, Keyword: "更新系统", Limit: 10),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.Success);
        Assert.Equal(1, result.NodesSucceeded);
        Assert.Equal(2, node.Total);
        var entry = Assert.Single(node.Entries);
        Assert.Contains("更新系统", entry.Message, StringComparison.Ordinal);
        Assert.Equal("Success", entry.Level);
        Assert.Equal("settings", entry.Service);
        Assert.NotNull(entry.Time);
        Assert.NotNull(entry.Timestamp);
        Assert.Contains("/core/settings/upgrade", entry.Detail!, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":10", script.RequestBodies[LogRoutes.PanelOperation], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PanelOperation_LimitTruncatesRows()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        for (var index = 0; index < 3; index++)
        {
            script.OperationItems.Add(LogTestData.Operation("操作 " + index));
        }

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.Panel, Limit: 2),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.Equal(2, node.Entries.Count);
        Assert.True(node.Truncated);
        Assert.Equal(2, result.Limit);
    }

    [Fact]
    public async Task PanelLogin_MapsLoginEntryAndAppliesTimeRange()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.LoginItems.Add(LogTestData.Login(createdAt: DateTimeOffset.UtcNow.AddDays(-2)));
        script.LoginItems.Add(LogTestData.Login("Failed", DateTimeOffset.UtcNow));

        var result = await harness.Service.QueryAsync(
            new LogQuery(
                LogScope.Panel,
                PanelLog: "login",
                StartTime: DateTimeOffset.UtcNow.AddHours(-1)),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.Success);
        var entry = Assert.Single(node.Entries);
        Assert.Equal("Failed", entry.Level);
        Assert.Equal("登录", entry.Service);
        var body = script.RequestBodies[LogRoutes.PanelLogin];
        Assert.Contains("\"info\":", body, StringComparison.Ordinal);
        Assert.Contains("startTime", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_MapsItemsAndReportsHasMore()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.SystemItems.Add(LogTestData.System("[GIN] GET /api/v2/websites/1"));
        script.SystemItems.Add(LogTestData.System("docker started", service: "docker.service", priority: "4"));
        script.SystemHasMore = true;
        script.SystemNextCursor = "cursor-1";

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.System, Limit: 50),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.Success);
        Assert.Equal(2, node.Entries.Count);
        Assert.True(node.HasMore);
        Assert.Equal("cursor-1", node.NextCursor);
        Assert.True(node.Truncated);
        Assert.Equal("信息", node.Entries[0].Level);
        Assert.Equal("警告", node.Entries[1].Level);
        Assert.Equal("1panel-agent.service", node.Entries[0].Service);
        var body = script.RequestBodies[LogRoutes.SystemRead];
        Assert.Contains("\"pageSize\":50", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_KeywordIsSentToNodeAndAlsoFilteredLocally()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.SystemItems.Add(LogTestData.System("docker started", service: "docker.service"));
        script.SystemItems.Add(LogTestData.System("nginx reloaded", service: "nginx.service"));

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.System, Keyword: "docker"),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        var entry = Assert.Single(node.Entries);
        Assert.Equal("docker.service", entry.Service);
        Assert.Contains("\"keyword\":\"docker\"", script.RequestBodies[LogRoutes.SystemRead], StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_NodeFailureIsIsolated()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        harness.AddNode("node-b");
        harness.Script("node-a").SystemItems.Add(LogTestData.System("ok"));
        harness.Script("node-b").FailAll = true;

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.System),
            CancellationToken.None);

        Assert.Equal(2, result.NodesTotal);
        Assert.Equal(1, result.NodesSucceeded);
        Assert.Equal(1, result.NodesFailed);
        var failed = result.Nodes.Single(node => node.NodeName == "node-b");
        Assert.False(failed.Success);
        Assert.NotNull(failed.Error);
        Assert.Contains("不可达", failed.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Container_ReadsLinesAndAppliesLimit()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        harness.ContainerFactory.Script("node-a").Lines = ["a", "b", "c", "d"];

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.Container, Container: "1Panel-jenkins", Limit: 2),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.Success);
        Assert.Equal(2, node.Entries.Count);
        Assert.True(node.Truncated);
        Assert.Equal("1Panel-jenkins", node.Entries[0].Service);
        var query = harness.ContainerFactory.Script("node-a").LastQuery;
        Assert.NotNull(query);
        Assert.Equal("1Panel-jenkins", query!.Container);
        Assert.Equal(2, query.Tail);
    }

    [Fact]
    public async Task Container_RequiresContainerName()
    {
        var harness = new LogHarness();
        var node = harness.AddNode("node-a");

        var exception = await Assert.ThrowsAsync<LogValidationException>(
            () => harness.Service.QueryAsync(new LogQuery(LogScope.Container, NodeId: node.Id.Value), CancellationToken.None));

        Assert.Contains("容器", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Website_ResolvesDomainThenReadsLog()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.WebsiteItems.Add(LogTestData.Website(7, "example.com"));
        script.WebsiteLogContent = "127.0.0.1 - GET /a\n127.0.0.1 - GET /b";

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.Website, Website: "example.com", Limit: 10),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.Success);
        Assert.Equal(2, node.Entries.Count);
        Assert.Equal("example.com", node.Entries[0].Service);
        Assert.Equal("访问", node.Entries[0].Level);
        var searchBody = script.RequestBodies[LogRoutes.WebsiteSearch];
        Assert.Contains("\"name\":\"example.com\"", searchBody, StringComparison.Ordinal);
        var logBody = script.RequestBodies[LogRoutes.WebsiteLog];
        Assert.Contains("\"id\":7", logBody, StringComparison.Ordinal);
        Assert.Contains("\"logType\":\"access.log\"", logBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Website_ErrorLogType_IsMapped()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.WebsiteItems.Add(LogTestData.Website(7, "example.com"));
        script.WebsiteLogContent = "error line";

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.Website, Website: "7", LogType: "error"),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.Success);

        // 数字 id 不需要再查网站列表。
        Assert.False(script.RequestBodies.ContainsKey(LogRoutes.WebsiteSearch));
        Assert.Contains("\"logType\":\"error.log\"", script.RequestBodies[LogRoutes.WebsiteLog], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Website_UnknownDomain_ReportsNodeFailure()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.Website, Website: "missing.example"),
            CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.False(node.Success);
        Assert.Contains("未找到网站", node.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Website_RequiresWebsiteParameter()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");

        await Assert.ThrowsAsync<LogValidationException>(
            () => harness.Service.QueryAsync(new LogQuery(LogScope.Website), CancellationToken.None));
    }

    [Fact]
    public async Task Query_UnknownNode_ThrowsNotFound()
    {
        var harness = new LogHarness();

        await Assert.ThrowsAsync<LogNodeNotFoundException>(
            () => harness.Service.QueryAsync(
                new LogQuery(LogScope.Panel, NodeId: Guid.NewGuid()),
                CancellationToken.None));
    }

    [Fact]
    public async Task Query_DisabledNode_ThrowsNotFound()
    {
        var harness = new LogHarness();
        var node = harness.AddNode("node-a", enabled: false);

        await Assert.ThrowsAsync<LogNodeNotFoundException>(
            () => harness.Service.QueryAsync(
                new LogQuery(LogScope.Panel, NodeId: node.Id.Value),
                CancellationToken.None));
    }

    [Fact]
    public async Task Query_NoEnabledNodes_ReturnsEmptyResult()
    {
        var harness = new LogHarness();

        var result = await harness.Service.QueryAsync(new LogQuery(LogScope.Panel), CancellationToken.None);

        Assert.Equal(0, result.NodesTotal);
        Assert.Empty(result.Nodes);
        Assert.Equal(0, result.TotalEntries);
    }

    [Fact]
    public async Task Query_StartAfterEnd_ThrowsValidation()
    {
        var harness = new LogHarness();
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<LogValidationException>(
            () => harness.Service.QueryAsync(
                new LogQuery(LogScope.System, StartTime: now, EndTime: now.AddHours(-1)),
                CancellationToken.None));
    }

    [Fact]
    public async Task Query_LimitIsClampedToMaxLines()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");

        var result = await harness.Service.QueryAsync(
            new LogQuery(LogScope.Panel, Limit: 999_999),
            CancellationToken.None);

        Assert.Equal(harness.Options.MaxLines, result.Limit);
    }

    [Fact]
    public async Task GetFilterOptions_UnionsServicesAndCountsFailures()
    {
        var harness = new LogHarness();
        harness.AddNode("node-a");
        harness.AddNode("node-b");
        harness.Script("node-b").FailAll = true;
        harness.Script("node-a").Services = ["docker.service", "ssh.service"];
        harness.Script("node-a").Files = ["2026-11-16", "2026-11-15"];

        var options = await harness.Service.GetFilterOptionsAsync(null, CancellationToken.None);

        Assert.Equal(2, options.NodesTotal);
        Assert.Equal(1, options.NodesFailed);
        Assert.Equal(new[] { "docker.service", "ssh.service" }, options.Services);
        Assert.Equal(new[] { "2026-11-16", "2026-11-15" }, options.Files);
    }

    [Fact]
    public async Task GetFilterOptions_UnknownNode_ThrowsNotFound()
    {
        var harness = new LogHarness();

        await Assert.ThrowsAsync<LogNodeNotFoundException>(
            () => harness.Service.GetFilterOptionsAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
