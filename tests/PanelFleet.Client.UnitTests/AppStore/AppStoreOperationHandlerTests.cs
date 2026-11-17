using System.Text.Json;
using PanelFleet.Application.AppStore;
using PanelFleet.Application.Operations;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.AppStore;

/// <summary>应用商店写操作处理器单元测试：Plan 标题/摘要/风险/目标/警告，Execute 请求体与结果映射。</summary>
public sealed class AppStoreOperationHandlerTests
{
    private readonly AppInstallOperationHandler _install = new();
    private readonly AppUninstallOperationHandler _uninstall = new();
    private readonly AppUpdateOperationHandler _update = new();
    private readonly AppParamsUpdateOperationHandler _paramsUpdate = new();

    [Fact]
    public async Task Install_Plan_SingleNode_IsMedium_WithTargetsAndWarnings()
    {
        var node = AppStoreTestData.Node();
        var plan = await _install.PlanAsync(
            AppStoreTestData.PlanContext([node], AppStoreTestData.InstallParameters()),
            CancellationToken.None);

        Assert.Equal("app.install", _install.Kind);
        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("安装应用 MySQL", plan.Title, StringComparison.Ordinal);
        Assert.Contains("mysql", plan.Summary, StringComparison.Ordinal);
        Assert.Equal("app", plan.TargetType);
        Assert.Single(plan.Targets);
        Assert.Equal(node.Id, plan.Targets[0].NodeId);
        Assert.Equal(3, plan.Warnings.Count); // 3 条基础警告
    }

    [Fact]
    public async Task Install_Plan_Batch_IsCritical()
    {
        var nodes = new[] { AppStoreTestData.Node("node-a"), AppStoreTestData.Node("node-b") };
        var plan = await _install.PlanAsync(
            AppStoreTestData.PlanContext(nodes, AppStoreTestData.InstallParameters()),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Equal(2, plan.Targets.Count);
        Assert.Contains("2 个节点", plan.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Install_Plan_MissingAppDetailId_Throws(long? appDetailId)
    {
        var parameters = AppStoreTestData.InstallParameters();
        if (appDetailId is null)
        {
            parameters.Remove("appDetailId");
        }
        else
        {
            parameters["appDetailId"] = appDetailId.Value;
        }

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _install.PlanAsync(
                AppStoreTestData.PlanContext([AppStoreTestData.Node()], parameters),
                CancellationToken.None));

        Assert.Contains("appDetailId", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-bad")]
    [InlineData("has space")]
    [InlineData("中文名称")]
    public async Task Install_Plan_InvalidName_Throws(string name)
    {
        var parameters = AppStoreTestData.InstallParameters(name: name);

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _install.PlanAsync(
                AppStoreTestData.PlanContext([AppStoreTestData.Node()], parameters),
                CancellationToken.None));

        Assert.Contains("安装名称", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_Execute_PostsExpectedBody_AndHidesParameterValuesInDetail()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/install", """{"id":5,"name":"mysql"}""");
        var node = AppStoreTestData.Node();
        var client = factory.Create(AppStoreTestData.Descriptor(node));

        var step = await _install.ExecuteAsync(
            AppStoreTestData.NodeContext(node, AppStoreTestData.InstallParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.False(string.IsNullOrWhiteSpace(step.TaskId));
        Assert.Equal("mysql", step.TargetId);
        Assert.Contains("安装任务已提交", step.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", step.DetailJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("PANEL_DB_ROOT_PASSWORD", step.DetailJson ?? string.Empty, StringComparison.Ordinal);

        var call = Assert.Single(factory.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal(AppStoreRoutes.Install, call.Path);
        Assert.NotNull(call.Body);
        Assert.Contains("\"appDetailId\":2064", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"mysql\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"pullImage\":true", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"taskID\":", call.Body, StringComparison.Ordinal);
        Assert.Contains("PANEL_APP_PORT_HTTP", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_Execute_PanelError_ReturnsFailedStep()
    {
        var factory = new RecordingPanelClientFactory().Fail("apps/install", PanelErrorKind.Validation);
        var node = AppStoreTestData.Node();
        var client = factory.Create(AppStoreTestData.Descriptor(node));

        var step = await _install.ExecuteAsync(
            AppStoreTestData.NodeContext(node, AppStoreTestData.InstallParameters(), client),
            CancellationToken.None);

        Assert.False(step.Success);
        Assert.Equal(PanelErrorKind.Validation, step.Error?.Kind);
    }

    [Fact]
    public async Task Uninstall_Plan_SingleNodeIsHigh_BatchIsCritical()
    {
        var single = await _uninstall.PlanAsync(
            AppStoreTestData.PlanContext([AppStoreTestData.Node()], UninstallParameters()),
            CancellationToken.None);
        Assert.Equal(OperationRisk.High, single.Risk);
        Assert.Contains("卸载应用 komga", single.Title, StringComparison.Ordinal);
        Assert.Equal("app", single.TargetType);

        var batch = await _uninstall.PlanAsync(
            AppStoreTestData.PlanContext([AppStoreTestData.Node("a"), AppStoreTestData.Node("b")], UninstallParameters()),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Critical, batch.Risk);
    }

    [Fact]
    public async Task Uninstall_Plan_DangerousOptions_AddWarnings()
    {
        var parameters = UninstallParameters();
        parameters["deleteDB"] = true;
        parameters["deleteBackup"] = true;

        var plan = await _uninstall.PlanAsync(
            AppStoreTestData.PlanContext([AppStoreTestData.Node()], parameters),
            CancellationToken.None);

        Assert.Contains(plan.Warnings, warning => warning.Contains("数据库", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Contains("备份", StringComparison.Ordinal));
        Assert.Contains("数据库删除=是", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_Execute_PostsDeleteOperation()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/installed/op", "{}");
        var node = AppStoreTestData.Node();
        var client = factory.Create(AppStoreTestData.Descriptor(node));

        var step = await _uninstall.ExecuteAsync(
            AppStoreTestData.NodeContext(node, UninstallParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Equal("7", step.TargetId);
        Assert.False(string.IsNullOrWhiteSpace(step.TaskId));

        var call = Assert.Single(factory.Calls);
        Assert.Equal(AppStoreRoutes.InstalledOperate, call.Path);
        Assert.Contains("\"operate\":\"delete\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"installId\":7", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"deleteDB\":false", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_Plan_And_Execute_PostsUpgradeOperation()
    {
        var node = AppStoreTestData.Node();
        var updateParameters = new System.Text.Json.Nodes.JsonObject
        {
            ["installId"] = 3,
            ["name"] = "uptime-kuma",
            ["detailId"] = 9001,
            ["version"] = "1.24.0",
            ["backup"] = true,
            ["pullImage"] = false
        };

        var plan = await _update.PlanAsync(
            AppStoreTestData.PlanContext([node], updateParameters),
            CancellationToken.None);
        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Contains("升级应用 uptime-kuma", plan.Title, StringComparison.Ordinal);
        Assert.Contains("1.24.0", plan.Summary, StringComparison.Ordinal);

        var factory = new RecordingPanelClientFactory().Respond("apps/installed/op", "{}");
        var client = factory.Create(AppStoreTestData.Descriptor(node));
        var step = await _update.ExecuteAsync(
            AppStoreTestData.NodeContext(node, updateParameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.NotNull(body);
        Assert.Contains("\"operate\":\"upgrade\"", body, StringComparison.Ordinal);
        Assert.Contains("\"detailId\":9001", body, StringComparison.Ordinal);
        Assert.Contains("\"pullImage\":false", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_Plan_MissingDetailId_Throws()
    {
        var parameters = new System.Text.Json.Nodes.JsonObject { ["installId"] = 3 };
        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _update.PlanAsync(
                AppStoreTestData.PlanContext([AppStoreTestData.Node()], parameters),
                CancellationToken.None));
        Assert.Contains("detailId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParamsUpdate_Plan_IsMedium_AndSummaryListsKeys()
    {
        var plan = await _paramsUpdate.PlanAsync(
            AppStoreTestData.PlanContext([AppStoreTestData.Node()], ParamsUpdateParameters()),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("修改应用配置 komga", plan.Title, StringComparison.Ordinal);
        Assert.Contains("PANEL_APP_PORT_HTTP", plan.Summary, StringComparison.Ordinal);
        Assert.Equal("app", plan.TargetType);
    }

    [Fact]
    public async Task ParamsUpdate_Plan_RequiresParameters()
    {
        var parameters = new System.Text.Json.Nodes.JsonObject { ["installId"] = 3, ["params"] = new System.Text.Json.Nodes.JsonObject() };
        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _paramsUpdate.PlanAsync(
                AppStoreTestData.PlanContext([AppStoreTestData.Node()], parameters),
                CancellationToken.None));
        Assert.Contains("没有需要修改的参数", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParamsUpdate_Execute_PostsParams_AndAdvancedConfig()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/installed/params/update", "{}");
        var node = AppStoreTestData.Node();
        var client = factory.Create(AppStoreTestData.Descriptor(node));

        var step = await _paramsUpdate.ExecuteAsync(
            AppStoreTestData.NodeContext(node, ParamsUpdateParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Null(step.TaskId);
        Assert.Equal("3", step.TargetId);
        Assert.Contains("配置修改已提交", step.Message, StringComparison.Ordinal);

        var body = Assert.Single(factory.Calls).Body;
        Assert.NotNull(body);
        Assert.Contains("\"installId\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"params\":", body, StringComparison.Ordinal);
        Assert.Contains("PANEL_APP_PORT_HTTP", body, StringComparison.Ordinal);
        Assert.Contains("\"advanced\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"restartPolicy\":\"always\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParamsUpdate_Execute_Batch_IsCritical()
    {
        var plan = await _paramsUpdate.PlanAsync(
            AppStoreTestData.PlanContext(
                [AppStoreTestData.Node("a"), AppStoreTestData.Node("b")],
                ParamsUpdateParameters()),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
    }

    private static System.Text.Json.Nodes.JsonObject UninstallParameters() => new()
    {
        ["installId"] = 7,
        ["name"] = "komga",
        ["forceDelete"] = false,
        ["deleteBackup"] = false,
        ["deleteImage"] = false,
        ["deleteDB"] = false
    };

    private static System.Text.Json.Nodes.JsonObject ParamsUpdateParameters() => new()
    {
        ["installId"] = 3,
        ["name"] = "komga",
        ["params"] = new System.Text.Json.Nodes.JsonObject
        {
            ["PANEL_APP_PORT_HTTP"] = "25601"
        },
        ["advanced"] = true,
        ["cpuQuota"] = 0.5,
        ["memoryLimit"] = 512,
        ["memoryUnit"] = "M",
        ["restartPolicy"] = "always"
    };
}
