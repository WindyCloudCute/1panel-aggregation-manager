using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Containers;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Containers;

public sealed class ContainerOperationPlanTests
{
    [Fact]
    public async Task Start_SingleContainer_IsMedium_WithoutTypedConfirmation()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new ContainerStartHandler(Options());
        var context = PlanContext(handler.Kind, [node], new JsonObject { ["container"] = "web" });

        var plan = await handler.PlanAsync(context, CancellationToken.None);

        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Null(plan.TypedConfirmationText);
        Assert.Single(plan.Targets);
        Assert.Equal("container", plan.TargetType);
        Assert.Equal("容器启动", plan.Title);
    }

    [Fact]
    public async Task Start_BatchAcrossNodes_IsCritical_WithTypedTextAndBatchWarning()
    {
        var nodes = new[] { ContainerTestData.Node("node-a"), ContainerTestData.Node("node-b") };
        var handler = new ContainerStartHandler(Options());
        var context = PlanContext(handler.Kind, nodes, new JsonObject { ["container"] = "web" });

        var plan = await handler.PlanAsync(context, CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.False(string.IsNullOrWhiteSpace(plan.TypedConfirmationText));
        Assert.Contains("2个节点", plan.TypedConfirmationText!, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, warning => warning.Contains("批量", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_SingleContainer_IsHigh_WithVolumeWarning()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new ContainerDeleteHandler(Options());
        var context = PlanContext(handler.Kind, [node], new JsonObject { ["container"] = "web" });

        var plan = await handler.PlanAsync(context, CancellationToken.None);

        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Contains(plan.Warnings, warning => warning.Contains("匿名卷", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Contains("不可恢复", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_MultipleContainersOnSingleNode_IsCriticalBatch()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["containersByNode"] = new JsonObject
            {
                [node.Id.Value.ToString("N")] = new JsonArray("web", "worker")
            }
        };
        var handler = new ContainerDeleteHandler(Options());
        var context = PlanContext(handler.Kind, [node], parameters);

        var plan = await handler.PlanAsync(context, CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Contains("2个容器", plan.TypedConfirmationText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_ExceedingMaxContainers_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["containersByNode"] = new JsonObject
            {
                [node.Id.Value.ToString("N")] = new JsonArray("web", "worker")
            }
        };
        var handler = new ContainerStopHandler(Options(maxBatchContainers: 1));
        var context = PlanContext(handler.Kind, [node], parameters);

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(context, CancellationToken.None));

        Assert.Contains("最多操作 1 个容器", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidContainerName_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new ContainerStartHandler(Options());
        var context = PlanContext(handler.Kind, [node], new JsonObject { ["container"] = "web; rm -rf /" });

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task MissingContainerParameter_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new ContainerStartHandler(Options());
        var context = PlanContext(handler.Kind, [node], new JsonObject());

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(context, CancellationToken.None));

        Assert.Contains("容器名称", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainersByNode_WithUnknownNodeKey_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["containersByNode"] = new JsonObject
            {
                [Guid.NewGuid().ToString("N")] = new JsonArray("web")
            }
        };
        var handler = new ContainerStartHandler(Options());
        var context = PlanContext(handler.Kind, [node], parameters);

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task ComposeUp_IsMedium_AndComposeDown_IsHigh()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["compose"] = "webstack",
            ["path"] = "/opt/webstack/docker-compose.yml"
        };

        var up = await new ComposeUpHandler(Options()).PlanAsync(
            PlanContext("compose.up", [node], parameters),
            CancellationToken.None);
        var down = await new ComposeDownHandler(Options()).PlanAsync(
            PlanContext("compose.down", [node], parameters),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Medium, up.Risk);
        Assert.Equal(OperationRisk.High, down.Risk);
        Assert.Contains(down.Warnings, warning => warning.Contains("移除", StringComparison.Ordinal));
        Assert.Equal("compose", down.TargetType);
    }

    [Fact]
    public async Task Compose_WithoutPath_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new ComposeUpHandler(Options());
        var context = PlanContext(handler.Kind, [node], new JsonObject { ["compose"] = "webstack" });

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(context, CancellationToken.None));

        Assert.Contains("Compose 文件路径", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_IsHigh_AndRejectsBatch()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new ContainerRenameHandler();
        var parameters = new JsonObject { ["container"] = "web", ["newName"] = "web-2" };

        var plan = await handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None);
        Assert.Equal(OperationRisk.High, plan.Risk);

        var twoNodes = new[] { node, ContainerTestData.Node("node-b") };
        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, twoNodes, parameters), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteStop_SubmitsOperationAndConfirmsState()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory()
            .Map("containers/operate", "{}")
            .Map("containers/inspect", JsonSerializer.Serialize("""{"State":{"Status":"exited"}}"""));
        var handler = new ContainerStopHandler(Options(verifyTimeoutSeconds: 1));
        var parameters = new JsonObject { ["container"] = "web" };
        using var client = factory.Create(new NodeDescriptor(
            node.Id,
            node.Name,
            node.BaseUrl,
            "key",
            AuthMode.HmacSha256,
            TlsPolicy.Strict));

        var step = await handler.ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.False(string.IsNullOrWhiteSpace(step.TaskId));
        Assert.Contains("已确认容器状态：exited", step.Message, StringComparison.Ordinal);
        var operate = factory.Calls.First(call => call.Path.Contains("containers/operate", StringComparison.Ordinal));
        Assert.Contains("stop", operate.Body, StringComparison.Ordinal);
        Assert.Contains("web", operate.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteDelete_ConfirmsRemovalWhenInspectFails()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory().Map("containers/operate", "{}");
        var handler = new ContainerDeleteHandler(Options(verifyTimeoutSeconds: 1));
        var parameters = new JsonObject { ["container"] = "web" };
        using var client = factory.Create(new NodeDescriptor(
            node.Id,
            node.Name,
            node.BaseUrl,
            "key",
            AuthMode.HmacSha256,
            TlsPolicy.Strict));

        var step = await handler.ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("已确认容器已删除", step.Message, StringComparison.Ordinal);
        Assert.Contains("remove", factory.Calls.First(call => call.Path.Contains("containers/operate", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteBatchParameters_OnlyUsesCurrentNodeNames()
    {
        var nodeA = ContainerTestData.Node("node-a");
        var nodeB = ContainerTestData.Node("node-b");
        var factory = new FakePanelClientFactory()
            .Map("containers/operate", "{}")
            .Map("containers/inspect", JsonSerializer.Serialize("""{"State":{"Status":"running"}}"""));
        var parameters = new JsonObject
        {
            ["containersByNode"] = new JsonObject
            {
                [nodeA.Id.Value.ToString("N")] = new JsonArray("web"),
                [nodeB.Id.Value.ToString("N")] = new JsonArray("worker")
            }
        };
        var handler = new ContainerStartHandler(Options(verifyTimeoutSeconds: 1));
        using var client = factory.Create(new NodeDescriptor(
            nodeA.Id,
            nodeA.Name,
            nodeA.BaseUrl,
            "key",
            AuthMode.HmacSha256,
            TlsPolicy.Strict));

        var step = await handler.ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), nodeA, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        var operate = factory.Calls.First(call => call.Path.Contains("containers/operate", StringComparison.Ordinal));
        Assert.Contains("web", operate.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("worker", operate.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeRebuild_IsHigh_AndExecutesRebuildOperation()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject { ["compose"] = "webstack", ["path"] = "/opt/webstack" };
        var plan = await new ComposeRebuildHandler(Options()).PlanAsync(
            PlanContext("compose.rebuild", [node], parameters),
            CancellationToken.None);

        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Equal("Compose 重建", plan.Title);
        Assert.Equal("compose", plan.TargetType);

        var factory = new FakePanelClientFactory().Map("containers/compose/operate", "{}");
        using var client = factory.Create(new NodeDescriptor(
            node.Id, node.Name, node.BaseUrl, "key", AuthMode.HmacSha256, TlsPolicy.Strict));
        var step = await new ComposeRebuildHandler(Options()).ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        var call = factory.Calls.First(item => item.Path.Contains("containers/compose/operate", StringComparison.Ordinal));
        Assert.Contains("\"operation\":\"rebuild\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"webstack\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeCreate_SingleNode_IsHigh_WithoutTypedConfirmation()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject { ["compose"] = "webstack", ["content"] = "services: {}" };
        var handler = new ComposeCreateHandler();

        var plan = await handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None);

        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Null(plan.TypedConfirmationText);
        Assert.Contains(plan.Warnings, warning => warning.Contains("异步", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComposeCreate_Batch_IsCritical_WithCreateTypedText()
    {
        var nodes = new[] { ContainerTestData.Node("node-a"), ContainerTestData.Node("node-b") };
        var parameters = new JsonObject { ["compose"] = "webstack", ["content"] = "services: {}" };
        var handler = new ComposeCreateHandler();

        var plan = await handler.PlanAsync(PlanContext(handler.Kind, nodes, parameters), CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Equal("确认创建2个节点的Compose项目", plan.TypedConfirmationText);
    }

    [Fact]
    public async Task ComposeCreate_InvalidName_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject { ["compose"] = "Web Stack", ["content"] = "services: {}" };
        var handler = new ComposeCreateHandler();

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));

        Assert.Contains("小写字母", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeCreate_MissingContent_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject { ["compose"] = "webstack" };
        var handler = new ComposeCreateHandler();

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));

        Assert.Contains("Compose 文件内容", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeCreate_OversizeContent_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["compose"] = "webstack",
            ["content"] = new string('a', 256 * 1024 + 1)
        };
        var handler = new ComposeCreateHandler();

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));

        Assert.Contains("256 KiB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeCreate_ExecutePostsCreateRequest()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject { ["compose"] = "webstack", ["content"] = "services: {}" };
        var factory = new FakePanelClientFactory().Map("containers/compose", "{}");
        using var client = factory.Create(new NodeDescriptor(
            node.Id, node.Name, node.BaseUrl, "key", AuthMode.HmacSha256, TlsPolicy.Strict));

        var step = await new ComposeCreateHandler().ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.False(string.IsNullOrWhiteSpace(step.TaskId));
        Assert.Equal(32, step.TaskId!.Length);
        var call = factory.Calls.First(item => item.Path.EndsWith("containers/compose", StringComparison.Ordinal));
        Assert.Contains("\"from\":\"edit\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"webstack\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"file\":\"services: {}\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"forcePull\":false", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeUpdate_DetailPathOutsideProjectDirectory_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["compose"] = "webstack",
            ["path"] = "/opt/webstack",
            ["detailPath"] = "/etc/docker-compose.yml",
            ["content"] = "services: {}"
        };
        var handler = new ComposeUpdateHandler();

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));

        Assert.Contains("项目目录下", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeUpdate_ExecutePostsUpdateRequestWithDetailPath()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["compose"] = "webstack",
            ["path"] = "/opt/webstack",
            ["detailPath"] = "/opt/webstack/docker-compose.yml",
            ["content"] = "services: {}"
        };
        var factory = new FakePanelClientFactory().Map("containers/compose/update", "{}");
        using var client = factory.Create(new NodeDescriptor(
            node.Id, node.Name, node.BaseUrl, "key", AuthMode.HmacSha256, TlsPolicy.Strict));

        var step = await new ComposeUpdateHandler().ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.False(string.IsNullOrWhiteSpace(step.TaskId));
        var call = factory.Calls.First(item => item.Path.Contains("containers/compose/update", StringComparison.Ordinal));
        Assert.Contains("\"detailPath\":\"/opt/webstack/docker-compose.yml\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"services: {}\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeDelete_IsCritical_AndRequiresProjectNameTypedConfirmation()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["compose"] = "webstack",
            ["path"] = "/opt/webstack",
            ["withFile"] = true
        };
        var handler = new ComposeDeleteHandler();

        var plan = await handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Equal("webstack", plan.TypedConfirmationText);
        Assert.Contains(plan.Warnings, warning => warning.Contains("不可撤销", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Contains("项目目录", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComposeDelete_ExecutePostsDeleteOperationWithFileAndForce()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["compose"] = "webstack",
            ["path"] = "/opt/webstack",
            ["withFile"] = true,
            ["force"] = true
        };
        var factory = new FakePanelClientFactory().Map("containers/compose/operate", "{}");
        using var client = factory.Create(new NodeDescriptor(
            node.Id, node.Name, node.BaseUrl, "key", AuthMode.HmacSha256, TlsPolicy.Strict));

        var step = await new ComposeDeleteHandler().ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Null(step.TaskId);
        var call = factory.Calls.First(item => item.Path.Contains("containers/compose/operate", StringComparison.Ordinal));
        Assert.Contains("\"operation\":\"delete\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"withFile\":true", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"force\":true", call.Body, StringComparison.Ordinal);
    }

    private static ContainerOptions Options(
        int maxBatchContainers = 20,
        int verifyTimeoutSeconds = 1) => new()
    {
        MaxBatchContainers = maxBatchContainers,
        VerifyTimeoutSeconds = verifyTimeoutSeconds
    };

    private static OperationPlanContext PlanContext(
        string kind,
        IReadOnlyList<NodeRecord> nodes,
        JsonObject parameters)
    {
        var invocation = new OperationInvocation(kind, nodes.Select(node => node.Id).ToArray(), parameters);
        return new OperationPlanContext(invocation, nodes, OperationParameters.From(parameters));
    }
}
