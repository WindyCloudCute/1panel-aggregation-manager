using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client.UnitTests.Overview;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Operations;

public sealed class WriteOperationServiceTests
{
    private sealed class Harness
    {
        public Harness(Action<OperationOptions>? configure = null, params NodeRecord[] nodes)
        {
            NodeStore.Nodes.AddRange(nodes);
            Options = new OperationOptions();
            configure?.Invoke(Options);
            Time = new TestTimeProvider(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
            Service = new WriteOperationService(
                NodeStore,
                new FakeSecretProtector(),
                new FakePanelClientFactory(),
                new OperationHandlerRegistry([Handler]),
                new ConfirmationStore(Time),
                new OperationHistoryStore(Options),
                Guards,
                Audit,
                Options,
                Time,
                NullLogger<WriteOperationService>.Instance);
        }

        public FakeNodeStore NodeStore { get; } = new();

        public RecordingOperationHandler Handler { get; } = new();

        public FakeAuditStore Audit { get; } = new();

        public OperationGuards Guards { get; } = new();

        public OperationOptions Options { get; }

        public TestTimeProvider Time { get; }

        public WriteOperationService Service { get; }

        public OperationInvocation Invocation(params NodeRecord[] nodes) =>
            new("test.run", nodes.Select(node => node.Id).ToArray());
    }

    private static readonly ActorInfo Actor = OperationTestData.Actor();

    [Fact]
    public async Task Preview_BatchHighRisk_RequiresTypedConfirmationWithNodeCount()
    {
        var nodeA = OperationTestData.Node("node-a");
        var nodeB = OperationTestData.Node("node-b");
        var harness = new Harness(null, nodeA, nodeB);
        harness.Handler.Risk = OperationRisk.High;

        var preview = await harness.Service.PreviewAsync(harness.Invocation(nodeA, nodeB), Actor, default);

        Assert.True(preview.RequiresTypedConfirmation);
        Assert.Equal("2 个节点", preview.ConfirmText);
        Assert.Equal(2, preview.Targets.Count);
        Assert.Equal(OperationRisk.High, preview.Risk);
    }

    [Fact]
    public async Task Preview_SingleCriticalRisk_UsesNodeNameAsConfirmText()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);
        harness.Handler.Risk = OperationRisk.Critical;

        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);

        Assert.True(preview.RequiresTypedConfirmation);
        Assert.Equal("node-a", preview.ConfirmText);
    }

    [Fact]
    public async Task Preview_LowRisk_DoesNotRequireTypedConfirmation()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);

        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);

        Assert.False(preview.RequiresTypedConfirmation);
        Assert.Null(preview.ConfirmText);
        Assert.Equal(OperationRisk.Low, preview.Risk);
    }

    [Fact]
    public async Task Preview_UnknownKind_ThrowsNotSupported()
    {
        var harness = new Harness();

        var exception = await Assert.ThrowsAsync<OperationNotSupportedException>(
            () => harness.Service.PreviewAsync(
                new OperationInvocation("unknown.kind", [OperationTestData.Node("x").Id]),
                Actor,
                default));

        Assert.Contains("unknown.kind", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_NoNodes_ThrowsValidation()
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<OperationValidationException>(
            () => harness.Service.PreviewAsync(new OperationInvocation("test.run", []), Actor, default));
    }

    [Fact]
    public async Task Preview_DisabledNode_ThrowsValidation()
    {
        var node = OperationTestData.Node("disabled-node", enabled: false);
        var harness = new Harness(null, node);

        await Assert.ThrowsAsync<OperationValidationException>(
            () => harness.Service.PreviewAsync(harness.Invocation(node), Actor, default));
    }

    [Fact]
    public async Task Preview_TooManyNodes_ThrowsValidation()
    {
        var nodes = new[]
        {
            OperationTestData.Node("node-a"),
            OperationTestData.Node("node-b"),
            OperationTestData.Node("node-c")
        };
        var harness = new Harness(options => options.MaxBatchNodes = 2, nodes);

        await Assert.ThrowsAsync<OperationValidationException>(
            () => harness.Service.PreviewAsync(harness.Invocation(nodes), Actor, default));
    }

    [Fact]
    public async Task Execute_TypedConfirmationMismatch_RejectedAndAudited()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);
        harness.Handler.Risk = OperationRisk.High;

        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);

        var exception = await Assert.ThrowsAsync<OperationConfirmationException>(
            () => harness.Service.ExecuteAsync(preview.TokenId, "wrong", Actor, default));

        Assert.Equal("typed_confirmation_mismatch", exception.Reason);
        Assert.Contains("node-a", exception.Message, StringComparison.Ordinal);
        Assert.Contains(harness.Audit.Entries, entry => entry.Action == "operation.rejected");
        Assert.Empty(harness.Handler.ExecutedNodes);
    }

    [Fact]
    public async Task Execute_TokenIsSingleUse()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);
        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);

        var execution = await harness.Service.ExecuteAsync(preview.TokenId, null, Actor, default);
        Assert.Equal(OperationStatus.Succeeded, execution.Status);

        await Assert.ThrowsAsync<OperationConfirmationException>(
            () => harness.Service.ExecuteAsync(preview.TokenId, null, Actor, default));
    }

    [Fact]
    public async Task Execute_Success_AuditsEachNodeAndStoresHistory()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);
        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);

        var execution = await harness.Service.ExecuteAsync(preview.TokenId, null, Actor, default);

        Assert.Equal(OperationStatus.Succeeded, execution.Status);
        Assert.Single(execution.Steps);
        Assert.True(execution.Steps[0].Success);
        Assert.Equal("node-a", execution.Steps[0].NodeName);
        Assert.Single(harness.Audit.Entries);
        Assert.Equal("operation.executed", harness.Audit.Entries[0].Action);
        Assert.Equal(node.Id, harness.Audit.Entries[0].NodeId);
        Assert.NotNull(harness.Service.GetExecution(execution.Id));
        Assert.Contains(harness.Service.ListRecent(10), item => item.Id == execution.Id);
    }

    [Fact]
    public async Task Execute_BatchStopsAfterFailure()
    {
        var nodeA = OperationTestData.Node("node-a");
        var nodeB = OperationTestData.Node("node-b");
        var harness = new Harness(null, nodeA, nodeB);
        harness.Handler.Execute = (context, _) => Task.FromResult(new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            context.Node.Name != "node-a",
            context.Node.Name == "node-a" ? "模拟失败" : "成功",
            null,
            null,
            null,
            TimeSpan.Zero));

        var preview = await harness.Service.PreviewAsync(harness.Invocation(nodeA, nodeB), Actor, default);
        var execution = await harness.Service.ExecuteAsync(preview.TokenId, null, Actor, default);

        Assert.Equal(OperationStatus.Failed, execution.Status);
        Assert.Single(execution.Steps);
        Assert.Contains("模拟失败", execution.Steps[0].Message, StringComparison.Ordinal);
        Assert.Equal(["node-a"], harness.Handler.ExecutedNodes);
        Assert.Contains(harness.Audit.Entries, entry => entry.Action == "operation.failed");
        Assert.Contains(harness.Audit.Entries, entry => entry.Action == "operation.batch");
    }

    [Fact]
    public async Task Execute_DuplicateWithinWindow_Rejected()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);

        var first = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);
        await harness.Service.ExecuteAsync(first.TokenId, null, Actor, default);

        var second = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);
        var exception = await Assert.ThrowsAsync<OperationConfirmationException>(
            () => harness.Service.ExecuteAsync(second.TokenId, null, Actor, default));

        Assert.Equal("duplicate", exception.Reason);
        Assert.Single(harness.Handler.ExecutedNodes);
    }

    [Fact]
    public async Task Execute_TokenBoundToActor()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);
        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);

        var otherActor = OperationTestData.Actor("user-2");
        var exception = await Assert.ThrowsAsync<OperationConfirmationException>(
            () => harness.Service.ExecuteAsync(preview.TokenId, null, otherActor, default));

        Assert.Equal("expired", exception.Reason);
        Assert.Empty(harness.Handler.ExecutedNodes);
    }

    [Fact]
    public async Task Execute_ExpiredToken_Rejected()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(options => options.ConfirmationTtlSeconds = 60, node);
        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);

        harness.Time.Advance(TimeSpan.FromSeconds(61));

        var exception = await Assert.ThrowsAsync<OperationConfirmationException>(
            () => harness.Service.ExecuteAsync(preview.TokenId, null, Actor, default));

        Assert.Equal("expired", exception.Reason);
    }

    [Fact]
    public async Task Execute_NodeLocked_BatchStopped()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(options => options.NodeLockTimeoutSeconds = 1, node);
        using var lease = await harness.Guards.TryAcquireNodeAsync(node.Id, TimeSpan.FromSeconds(1), default);
        Assert.NotNull(lease);

        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);
        var execution = await harness.Service.ExecuteAsync(preview.TokenId, null, Actor, default);

        Assert.Equal(OperationStatus.Failed, execution.Status);
        Assert.Contains("其他写操作", execution.Steps[0].Message, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.ExecutedNodes);
    }

    [Fact]
    public async Task Execute_HandlerException_RecordedAsFailedStep()
    {
        var node = OperationTestData.Node("node-a");
        var harness = new Harness(null, node);
        harness.Handler.Execute = (_, _) => throw new InvalidOperationException("模拟异常");

        var preview = await harness.Service.PreviewAsync(harness.Invocation(node), Actor, default);
        var execution = await harness.Service.ExecuteAsync(preview.TokenId, null, Actor, default);

        Assert.Equal(OperationStatus.Failed, execution.Status);
        Assert.Contains("执行异常", execution.Steps[0].Message, StringComparison.Ordinal);
        Assert.Contains(harness.Audit.Entries, entry => entry.Action == "operation.failed");
    }

    [Fact]
    public void Parameters_MissingRequiredValue_ThrowsValidation()
    {
        var parameters = OperationParameters.From(null);

        var exception = Assert.Throws<OperationValidationException>(
            () => parameters.RequireString("name", "容器名"));

        Assert.Contains("容器名", exception.Message, StringComparison.Ordinal);
    }
}
