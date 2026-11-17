using System.Text.Json.Nodes;
using PanelFleet.Application.AppStore;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.AppStore;

/// <summary>跨节点批量升级处理器单元测试：风险/确认文本/目标校验，以及串行调用与失败停止。</summary>
public sealed class AppBatchUpgradeOperationHandlerTests
{
    private readonly AppBatchUpgradeOperationHandler _handler = new();

    [Fact]
    public async Task Plan_SingleApp_IsHigh_WithTypedConfirmation()
    {
        var node = AppStoreTestData.Node();
        var parameters = BatchParameters(Target(node, 3, "uptime-kuma", "1.24.0", 9001));

        var plan = await _handler.PlanAsync(
            AppStoreTestData.PlanContext([node], parameters, "app.batch_upgrade"),
            CancellationToken.None);

        Assert.Equal("app.batch_upgrade", _handler.Kind);
        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Contains("升级应用 uptime-kuma", plan.Title, StringComparison.Ordinal);
        Assert.Equal("升级 1 个节点 1 个应用", plan.TypedConfirmationText);
        Assert.Equal("app", plan.TargetType);
    }

    [Fact]
    public async Task Plan_MultipleNodesAndApps_IsCritical_AndSummarizesEachNode()
    {
        var nodeA = AppStoreTestData.Node("node-a");
        var nodeB = AppStoreTestData.Node("node-b");
        var parameters = BatchParameters(
            Target(nodeA, 3, "uptime-kuma", "1.24.0", 9001),
            Target(nodeA, 4, "komga", "1.29.0", 9002),
            Target(nodeB, 5, "mysql", "8.4.13", 9003));

        var plan = await _handler.PlanAsync(
            AppStoreTestData.PlanContext([nodeA, nodeB], parameters, "app.batch_upgrade"),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Equal("批量升级应用（3 个）", plan.Title);
        Assert.Equal("升级 2 个节点 3 个应用", plan.TypedConfirmationText);
        Assert.Contains("node-a", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("node-b", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("1.24.0", plan.Summary, StringComparison.Ordinal);
        Assert.Equal(4, plan.Warnings.Count);
        Assert.Equal(2, plan.Targets.Count);
    }

    [Fact]
    public async Task Plan_UnknownNode_Throws()
    {
        var node = AppStoreTestData.Node();
        var unknown = Guid.NewGuid();
        var parameters = BatchParameters(
            (unknown, 3, "uptime-kuma", "1.24.0", 9001));

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _handler.PlanAsync(
                AppStoreTestData.PlanContext([node], parameters, "app.batch_upgrade"),
                CancellationToken.None));

        Assert.Contains("不在本次操作范围内", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_MissingTargets_Throws()
    {
        var parameters = new JsonObject { ["backup"] = true };

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _handler.PlanAsync(
                AppStoreTestData.PlanContext([AppStoreTestData.Node()], parameters, "app.batch_upgrade"),
                CancellationToken.None));

        Assert.Contains("targets", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_MissingDetailId_Throws()
    {
        var node = AppStoreTestData.Node();
        var parameters = BatchParameters(Target(node, 3, "uptime-kuma", "1.24.0", 0));

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _handler.PlanAsync(
                AppStoreTestData.PlanContext([node], parameters, "app.batch_upgrade"),
                CancellationToken.None));

        Assert.Contains("detailId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_SequentiallyPostsEveryUpgrade_ForCurrentNode()
    {
        var nodeA = AppStoreTestData.Node("node-a");
        var nodeB = AppStoreTestData.Node("node-b");
        var parameters = BatchParameters(
            Target(nodeA, 3, "uptime-kuma", "1.24.0", 9001),
            Target(nodeA, 4, "komga", "1.29.0", 9002),
            Target(nodeB, 5, "mysql", "8.4.13", 9003));

        var factory = new RecordingPanelClientFactory().Respond("apps/installed/op", "{}");
        var client = factory.Create(AppStoreTestData.Descriptor(nodeA));

        var step = await _handler.ExecuteAsync(
            AppStoreTestData.NodeContext(nodeA, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("已提交 2 个应用升级任务", step.Message, StringComparison.Ordinal);
        Assert.Equal("node-a", step.NodeName);
        Assert.NotNull(step.DetailJson);
        Assert.Contains("\"taskIds\"", step.DetailJson, StringComparison.Ordinal);

        var calls = factory.Calls;
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(AppStoreRoutes.InstalledOperate, call.Path));
        Assert.Contains("\"installId\":3", calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"installId\":4", calls[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"operate\":\"upgrade\"", calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"detailId\":9001", calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"backup\":true", calls[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_StopsAtFirstFailure_AndReturnsFailedStep()
    {
        var nodeA = AppStoreTestData.Node("node-a");
        var nodeB = AppStoreTestData.Node("node-b");
        var parameters = BatchParameters(
            Target(nodeA, 3, "uptime-kuma", "1.24.0", 9001),
            Target(nodeA, 4, "komga", "1.29.0", 9002),
            Target(nodeB, 5, "mysql", "8.4.13", 9003));

        var factory = new RecordingPanelClientFactory()
            .Respond("apps/installed/op", "{}")
            .FailAfterCalls("apps/installed/op", 1, PanelErrorKind.Network);
        var client = factory.Create(AppStoreTestData.Descriptor(nodeA));

        var step = await _handler.ExecuteAsync(
            AppStoreTestData.NodeContext(nodeA, parameters, client),
            CancellationToken.None);

        Assert.False(step.Success);
        Assert.Contains("失败", step.Message, StringComparison.Ordinal);
        Assert.NotNull(step.Error);
        Assert.Equal(2, factory.Calls.Count);
        Assert.DoesNotContain(factory.Calls, call => call.Body?.Contains("installId\":5", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Execute_NodeWithoutTargets_ReturnsFailedStep()
    {
        var nodeA = AppStoreTestData.Node("node-a");
        var nodeB = AppStoreTestData.Node("node-b");
        var parameters = BatchParameters(Target(nodeA, 3, "uptime-kuma", "1.24.0", 9001));

        var factory = new RecordingPanelClientFactory().Respond("apps/installed/op", "{}");
        var client = factory.Create(AppStoreTestData.Descriptor(nodeB));

        var step = await _handler.ExecuteAsync(
            AppStoreTestData.NodeContext(nodeB, parameters, client),
            CancellationToken.None);

        Assert.False(step.Success);
        Assert.Contains("没有待升级应用", step.Message, StringComparison.Ordinal);
        Assert.Empty(factory.Calls);
    }

    private static JsonObject BatchParameters(params (Guid NodeId, long InstallId, string? Name, string Version, long DetailId)[] targets)
    {
        var array = new JsonArray();
        foreach (var target in targets)
        {
            array.Add(new JsonObject
            {
                ["nodeId"] = target.NodeId.ToString("D"),
                ["installId"] = target.InstallId,
                ["name"] = target.Name,
                ["version"] = target.Version,
                ["detailId"] = target.DetailId
            });
        }

        return new JsonObject
        {
            ["backup"] = true,
            ["pullImage"] = true,
            ["deleteImage"] = false,
            ["targets"] = array
        };
    }

    private static (Guid NodeId, long InstallId, string? Name, string Version, long DetailId) Target(
        NodeRecord node,
        long installId,
        string name,
        string version,
        long detailId) => (node.Id.Value, installId, name, version, detailId);
}
