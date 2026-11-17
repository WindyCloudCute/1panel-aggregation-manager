using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Application.PanelUpdates;
using PanelFleet.Client;
using PanelFleet.Client.UnitTests.Operations;
using PanelFleet.Client.UnitTests.Overview;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.PanelUpdates;

public sealed class PanelUpdateOperationHandlerTests
{
    private sealed class Harness
    {
        public Harness()
        {
            Updates = new PanelUpdateService(
                Nodes,
                new FakeSecretProtector(),
                Factory,
                UpdateOptions,
                Time,
                NullLogger<PanelUpdateService>.Instance);
            Handler = new PanelUpdateOperationHandler(
                Updates,
                UpdateOptions,
                NullLogger<PanelUpdateOperationHandler>.Instance);
            Kernel = new WriteOperationService(
                Nodes,
                new FakeSecretProtector(),
                Factory,
                new OperationHandlerRegistry([Handler]),
                new ConfirmationStore(Time),
                new OperationHistoryStore(KernelOptions),
                Guards,
                Audit,
                KernelOptions,
                Time,
                NullLogger<WriteOperationService>.Instance);
        }

        public ScriptedPanelClientFactory Factory { get; } = new();

        public FakeNodeStore Nodes { get; } = new();

        public FakeAuditStore Audit { get; } = new();

        public OperationOptions KernelOptions { get; } = new();

        public PanelUpdateOptions UpdateOptions { get; } = new()
        {
            CheckTimeoutSeconds = 5,
            HealthCheckTimeoutSeconds = 5,
            HealthCheckIntervalSeconds = 1
        };

        public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

        public OperationGuards Guards { get; } = new();

        public PanelUpdateService Updates { get; }

        public PanelUpdateOperationHandler Handler { get; }

        public WriteOperationService Kernel { get; }

        public NodeRecord AddNode(string name)
        {
            var node = PanelUpdateTestData.Node(name, Factory);
            Nodes.Nodes.Add(node);
            return node;
        }

        public PanelUpdateNodeScript Script(string name) => Factory.Script(name);

        public OperationInvocation Invocation(params NodeRecord[] nodes) => new(
            PanelUpdateOperationHandler.HandlerKind,
            nodes.Select(node => node.Id).ToArray());

        public Task<OperationStepResult> ExecuteAsync(NodeRecord node, JsonObject? parameters = null)
        {
            var client = Factory.Create(new NodeDescriptor(
                node.Id,
                node.Name,
                node.BaseUrl,
                "fake-api-key",
                AuthMode.HmacSha256,
                TlsPolicy.Strict));
            var context = new OperationNodeContext(
                Guid.NewGuid(),
                node,
                client,
                OperationParameters.From(parameters));
            return Handler.ExecuteAsync(context, CancellationToken.None);
        }

        public Task<OperationPlan> PlanAsync(
            IReadOnlyList<NodeRecord> nodes,
            JsonObject? parameters = null)
        {
            var invocation = new OperationInvocation(
                PanelUpdateOperationHandler.HandlerKind,
                nodes.Select(node => node.Id).ToArray(),
                parameters);
            return Handler.PlanAsync(
                new OperationPlanContext(invocation, nodes, OperationParameters.From(parameters)),
                CancellationToken.None);
        }
    }

    private static readonly ActorInfo Actor = PanelUpdateTestData.Actor();

    [Fact]
    public async Task Plan_SingleNode_IsCriticalWithDerivedTargetVersion()
    {
        var harness = new Harness();
        var node = harness.AddNode("node-a");
        harness.Script("node-a").VersionAfterUpgrade = "v2.5.1";

        var plan = await harness.PlanAsync([node]);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Equal("panel", plan.TargetType);
        Assert.Null(plan.TypedConfirmationText);
        var target = Assert.Single(plan.Targets);
        Assert.Equal(node.Id, target.NodeId);
        Assert.Contains("v2.5.1", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("v2.3.2", plan.Summary, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, warning => warning.Contains("无自动回滚", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Contains("维护窗口", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_Batch_UsesNodeCountAsTypedConfirmationText()
    {
        var harness = new Harness();
        var nodeA = harness.AddNode("node-a");
        var nodeB = harness.AddNode("node-b");

        var plan = await harness.PlanAsync([nodeA, nodeB]);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Equal("升级 2 个节点", plan.TypedConfirmationText);
        Assert.Equal(2, plan.Targets.Count);
        Assert.Contains(plan.Warnings, warning => warning.Contains("立即停止", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Contains("按所选顺序", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_AlreadyLatest_ThrowsValidation()
    {
        var harness = new Harness();
        var node = harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.CurrentVersion = "v2.5.1";
        script.LatestVersion = "v2.5.1";

        var exception = await Assert.ThrowsAsync<OperationValidationException>(() => harness.PlanAsync([node]));

        Assert.Contains("已是最新", exception.Message, StringComparison.Ordinal);
        Assert.Contains("node-a", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_TargetVersionNotHigherThanCurrent_ThrowsValidation()
    {
        var harness = new Harness();
        var node = harness.AddNode("node-a");

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => harness.PlanAsync([node], new JsonObject { ["targetVersion"] = "v2.3.1" }));

        Assert.Contains("不高于", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_Success_SubmitsTargetAndReportsVersionChange()
    {
        var harness = new Harness();
        var node = harness.AddNode("node-a");
        harness.Script("node-a").VersionAfterUpgrade = "v2.5.1";

        var step = await harness.ExecuteAsync(node);

        Assert.True(step.Success);
        Assert.Contains("v2.3.2", step.Message, StringComparison.Ordinal);
        Assert.Contains("v2.5.1", step.Message, StringComparison.Ordinal);
        Assert.Contains("健康检查", step.Message, StringComparison.Ordinal);
        Assert.Equal(node.Id.Value.ToString(), step.TargetId);
        Assert.Equal(new[] { "v2.5.1" }, harness.Script("node-a").SubmittedVersions);
        Assert.NotNull(step.DetailJson);
        Assert.Contains("\"healthy\"", step.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"targetVersion\":\"v2.5.1\"", step.DetailJson, StringComparison.Ordinal);
        Assert.Null(step.Error);
        Assert.True(step.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Execute_VersionNotChanged_FailsWithNoRollbackHint()
    {
        var harness = new Harness();
        harness.UpdateOptions.HealthCheckTimeoutSeconds = 2;
        var node = harness.AddNode("node-a");
        harness.Script("node-a").VersionAfterUpgrade = "v2.3.2";

        var step = await harness.ExecuteAsync(node);

        Assert.False(step.Success);
        Assert.Contains("未回读到目标版本", step.Message, StringComparison.Ordinal);
        Assert.Contains("不会自动回滚", step.Message, StringComparison.Ordinal);
        Assert.NotNull(step.Error);
        Assert.NotNull(step.DetailJson);
        Assert.Contains("\"panelReachable\":true", step.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"healthy\"", step.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_PanelUnreachableAfterUpgrade_Fails()
    {
        var harness = new Harness();
        harness.UpdateOptions.HealthCheckTimeoutSeconds = 2;
        var node = harness.AddNode("node-a");
        var script = harness.Script("node-a");
        script.DashboardReachableAfterUpgrade = false;

        var step = await harness.ExecuteAsync(node);

        Assert.False(step.Success);
        Assert.Contains("面板不可达", step.Message, StringComparison.Ordinal);
        Assert.Equal(PanelErrorKind.Network, step.Error!.Kind);
        Assert.NotNull(step.DetailJson);
        Assert.Contains("\"panelReachable\":false", step.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_SubmitFails_ReturnsFailureWithoutHealthPolling()
    {
        var harness = new Harness();
        var node = harness.AddNode("node-a");
        harness.Script("node-a").UpgradeSubmitSucceeds = false;

        var step = await harness.ExecuteAsync(node);

        Assert.False(step.Success);
        Assert.Contains("提交升级命令失败", step.Message, StringComparison.Ordinal);
        Assert.Equal(1, harness.Script("node-a").HealthCalls); // 仅预检一次健康探测
        Assert.Single(harness.Script("node-a").SubmittedVersions);
    }

    [Fact]
    public async Task Kernel_Batch_TypedConfirmationRejectedThenSequentialFailureStops()
    {
        var harness = new Harness();
        var nodeA = harness.AddNode("node-a");
        var nodeB = harness.AddNode("node-b");
        var nodeC = harness.AddNode("node-c");
        harness.Script("node-a").VersionAfterUpgrade = "v2.5.1";
        harness.Script("node-b").UpgradeSubmitSucceeds = false;
        harness.Script("node-c").VersionAfterUpgrade = "v2.5.1";

        var wrongPreview = await harness.Kernel.PreviewAsync(
            harness.Invocation(nodeA, nodeB, nodeC),
            Actor,
            CancellationToken.None);

        Assert.Equal("升级 3 个节点", wrongPreview.ConfirmText);
        await Assert.ThrowsAsync<OperationConfirmationException>(
            () => harness.Kernel.ExecuteAsync(wrongPreview.TokenId, "升级 2 个节点", Actor, CancellationToken.None));

        var preview = await harness.Kernel.PreviewAsync(
            harness.Invocation(nodeA, nodeB, nodeC),
            Actor,
            CancellationToken.None);
        var execution = await harness.Kernel.ExecuteAsync(
            preview.TokenId,
            preview.ConfirmText,
            Actor,
            CancellationToken.None);

        Assert.Equal(OperationStatus.PartiallySucceeded, execution.Status);
        Assert.Equal(2, execution.Steps.Count);
        Assert.True(execution.Steps[0].Success);
        Assert.False(execution.Steps[1].Success);
        Assert.Equal(new[] { "v2.5.1" }, harness.Script("node-a").SubmittedVersions);
        Assert.Single(harness.Script("node-b").SubmittedVersions);
        Assert.Empty(harness.Script("node-c").SubmittedVersions);
        Assert.Single(
            harness.Audit.Entries,
            entry => entry.Action == "operation.batch");
        Assert.Contains(
            harness.Audit.Entries,
            entry => entry.Action == "operation.executed" && entry.TargetType == "panel");
        Assert.Contains(
            harness.Audit.Entries,
            entry => entry.Action == "operation.failed" && entry.TargetType == "panel");
    }
}
