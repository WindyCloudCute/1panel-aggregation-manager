using System.Text.Json.Nodes;
using PanelFleet.Application.Cronjobs;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Cronjobs;

public sealed class CronjobOperationHandlerTests
{
    private static readonly CronjobOptions Options = new();

    private static OperationPlanContext PlanContext(
        string kind,
        IReadOnlyList<NodeRecord> nodes,
        JsonObject parameters) =>
        new(
            new OperationInvocation(kind, nodes.Select(node => node.Id).ToArray(), parameters),
            nodes,
            OperationParameters.From(parameters));

    private static OperationNodeContext NodeContext(
        NodeRecord node,
        IPanelClient client,
        JsonObject parameters) =>
        new(Guid.NewGuid(), node, client, OperationParameters.From(parameters));

    private static JsonObject ShellPayload(string name = "daily-backup") => new()
    {
        ["name"] = name,
        ["type"] = CronjobTypes.Shell,
        ["groupID"] = 0,
        ["specCustom"] = false,
        ["spec"] = "30 1 * * 1",
        ["executor"] = "bash",
        ["scriptMode"] = "input",
        ["script"] = "echo hello",
        ["retainCopies"] = 7,
        ["retryTimes"] = 0,
        ["timeout"] = 3600,
        ["ignoreErr"] = false
    };

    [Fact]
    public async Task Create_Plan_IsMediumAndShowsName()
    {
        var node = CronjobTestData.Node("node-a");
        var handler = new CronjobCreateHandler(Options);

        var plan = await handler.PlanAsync(
            PlanContext("cronjob.create", [node], new JsonObject { ["payload"] = ShellPayload() }),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("daily-backup", plan.Summary, StringComparison.Ordinal);
        Assert.Null(plan.TypedConfirmationText);
        Assert.Equal("cronjob", plan.TargetType);
    }

    [Fact]
    public async Task Create_RequiresSingleNode()
    {
        var handler = new CronjobCreateHandler(Options);
        var nodes = new[] { CronjobTestData.Node("node-a"), CronjobTestData.Node("node-b") };

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(
                PlanContext("cronjob.create", nodes, new JsonObject { ["payload"] = ShellPayload() }),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("unknown-type")]
    public async Task Create_RejectsUnsupportedType(string type)
    {
        var handler = new CronjobCreateHandler(Options);
        var payload = ShellPayload();
        payload["type"] = type;

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(
                PlanContext("cronjob.create", [CronjobTestData.Node("node-a")], new JsonObject { ["payload"] = payload }),
                CancellationToken.None));
    }

    [Fact]
    public async Task Create_RequiresShellScript()
    {
        var handler = new CronjobCreateHandler(Options);
        var payload = ShellPayload();
        payload["script"] = "  ";

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(
                PlanContext("cronjob.create", [CronjobTestData.Node("node-a")], new JsonObject { ["payload"] = payload }),
                CancellationToken.None));
    }

    [Fact]
    public async Task Create_RequiresUrlForCurl()
    {
        var handler = new CronjobCreateHandler(Options);
        var payload = ShellPayload();
        payload["type"] = CronjobTypes.Curl;
        payload.Remove("script");
        payload["url"] = "";

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(
                PlanContext("cronjob.create", [CronjobTestData.Node("node-a")], new JsonObject { ["payload"] = payload }),
                CancellationToken.None));
    }

    [Fact]
    public async Task Create_Execute_PostsNormalizedPayload()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().Map(CronjobRoutes.Create, "{}");
        var handler = new CronjobCreateHandler(Options);

        var step = await handler.ExecuteAsync(
            NodeContext(node, factory.Create(Descriptor(node)), new JsonObject { ["payload"] = ShellPayload() }),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Equal("daily-backup", step.TargetId);
        var call = Assert.Single(factory.CallsFor(CronjobRoutes.Create));
        Assert.Contains("\"name\":\"daily-backup\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"shell\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"spec\":\"30 1 * * 1\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"retainCopies\":7", call.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", call.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_Execute_MergesCurrentFieldsAndPreservesSecret()
    {
        var node = CronjobTestData.Node("node-a");
        var current = """
            {"id":1,"name":"old-name","type":"snapshot","spec":"0 2 * * *","snapshotRule":{"withImage":true},"secret":"keep-me","retainCopies":7}
            """;
        var factory = new CronjobPanelClientFactory()
            .Map(CronjobRoutes.Info, current)
            .Map(CronjobRoutes.Update, "{}");
        var handler = new CronjobUpdateHandler(Options);

        var payload = new JsonObject
        {
            ["id"] = 1,
            ["name"] = "new-name",
            ["spec"] = "0 3 * * *",
            ["retainCopies"] = 5,
            ["retryTimes"] = 1,
            ["timeout"] = 600,
            ["ignoreErr"] = true,
            ["args"] = ""
        };

        var step = await handler.ExecuteAsync(
            NodeContext(node, factory.Create(Descriptor(node)), new JsonObject { ["payload"] = payload, ["id"] = 1 }),
            CancellationToken.None);

        Assert.True(step.Success);
        var call = Assert.Single(factory.CallsFor(CronjobRoutes.Update));
        Assert.Contains("\"name\":\"new-name\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"spec\":\"0 3 * * *\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"snapshot\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"snapshotRule\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"secret\":\"keep-me\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"retainCopies\":5", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_Plan_SingleIsHigh_BatchIsCriticalWithTypedText()
    {
        var handler = new CronjobDeleteHandler(Options);
        var first = CronjobTestData.Node("node-a");
        var second = CronjobTestData.Node("node-b");

        var single = await handler.PlanAsync(
            PlanContext("cronjob.delete", [first], new JsonObject { ["cronjob"] = 9 }),
            CancellationToken.None);
        Assert.Equal(OperationRisk.High, single.Risk);

        var batch = await handler.PlanAsync(
            PlanContext(
                "cronjob.delete",
                [first, second],
                new JsonObject
                {
                    ["cronjobsByNode"] = new JsonObject
                    {
                        [first.Id.Value.ToString("D")] = new JsonArray(1, 2),
                        [second.Id.Value.ToString("D")] = new JsonArray(3)
                    }
                }),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, batch.Risk);
        Assert.Equal("确认删除2个节点共3个计划任务", batch.TypedConfirmationText);
    }

    [Fact]
    public async Task Delete_Execute_UsesCleanDataFlag()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().Map(CronjobRoutes.Delete, "{}");
        var handler = new CronjobDeleteHandler(Options);

        var step = await handler.ExecuteAsync(
            NodeContext(
                node,
                factory.Create(Descriptor(node)),
                new JsonObject { ["cronjob"] = 9, ["cleanData"] = true, ["cleanRemoteData"] = false }),
            CancellationToken.None);

        Assert.True(step.Success);
        var call = Assert.Single(factory.CallsFor(CronjobRoutes.Delete));
        Assert.Contains("\"ids\":[9]", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"cleanData\":true", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_StopsAfterFirstFailure()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory()
            .Map(CronjobRoutes.Delete, "{}")
            .FailAtCall(
                CronjobRoutes.Delete,
                2,
                new PanelError(PanelErrorKind.PanelServerError, "服务错误: 删除失败", PanelCode: "500"));
        var handler = new CronjobDeleteHandler(Options);

        var step = await handler.ExecuteAsync(
            NodeContext(node, factory.Create(Descriptor(node)), new JsonObject { ["ids"] = new JsonArray(1, 2, 3) }),
            CancellationToken.None);

        Assert.False(step.Success);
        Assert.Contains("已成功处理 1 个任务后失败", step.Message, StringComparison.Ordinal);
        Assert.Equal(2, factory.CallsFor(CronjobRoutes.Delete).Count);
    }

    [Fact]
    public async Task EnableAndDisable_PostExpectedStatus()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().Map(CronjobRoutes.Status, "{}");

        var enabled = await new CronjobEnableHandler(Options).ExecuteAsync(
            NodeContext(node, factory.Create(Descriptor(node)), new JsonObject { ["cronjob"] = 3 }),
            CancellationToken.None);
        var disabled = await new CronjobDisableHandler(Options).ExecuteAsync(
            NodeContext(node, factory.Create(Descriptor(node)), new JsonObject { ["cronjob"] = 4 }),
            CancellationToken.None);

        Assert.True(enabled.Success);
        Assert.True(disabled.Success);
        var calls = factory.CallsFor(CronjobRoutes.Status);
        Assert.Equal(2, calls.Count);
        Assert.Contains("\"status\":\"Enable\"", calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Disable\"", calls[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunNow_MapsBusinessError()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory()
            .Map(CronjobRoutes.Handle, "{}")
            .FailNext(CronjobRoutes.Handle, new PanelError(PanelErrorKind.PanelServerError, "任务正在执行中", PanelCode: "500"));
        var handler = new CronjobRunNowHandler(Options);

        var step = await handler.ExecuteAsync(
            NodeContext(node, factory.Create(Descriptor(node)), new JsonObject { ["cronjob"] = 3 }),
            CancellationToken.None);

        Assert.False(step.Success);
        Assert.Contains("任务正在执行中", step.Message, StringComparison.Ordinal);
    }

    private static NodeDescriptor Descriptor(NodeRecord node) => new(
        node.Id,
        node.Name,
        node.BaseUrl,
        "api-key",
        node.AuthMode,
        node.TlsPolicy);
}
