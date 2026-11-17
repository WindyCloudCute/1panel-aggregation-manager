using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.AppStore;

/// <summary>应用安装写操作（<c>app.install</c>）：风险 Medium，批量 Critical；调用 <c>POST apps/install</c>。</summary>
public sealed class AppInstallOperationHandler : IOperationHandler
{
    public string Kind => "app.install";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadInstall(context.Parameters);
        var display = arguments.DisplayName ?? arguments.Name;
        var risk = AppStoreOperationSupport.BatchRisk(context.Nodes.Count, OperationRisk.Medium);

        var warnings = new List<string>
        {
            "安装会从 1Panel 应用商店下载镜像并创建容器，耗时取决于网络与磁盘。",
            "安装为异步任务，提交后请在“已安装应用”中刷新查看状态。",
            "已创建的容器与数据不会自动回滚；安装失败可手动卸载清理。"
        };

        if (arguments.Parameters.Count == 0)
        {
            warnings.Add("该应用没有额外安装参数，将使用默认配置。");
        }

        if (!string.IsNullOrWhiteSpace(arguments.RestartPolicy))
        {
            warnings.Add($"容器重启策略：{arguments.RestartPolicy}。");
        }

        var versionText = string.IsNullOrWhiteSpace(arguments.Version) ? string.Empty : $"，版本 {arguments.Version}";
        var summary =
            $"在 {context.Nodes.Count} 个节点安装 {display}（安装名称 {arguments.Name}{versionText}），"
            + $"应用参数 {arguments.Parameters.Count} 项。";

        return Task.FromResult(new OperationPlan(
            $"安装应用 {display}",
            summary,
            risk,
            AppStoreOperationSupport.Targets(context),
            warnings,
            TargetType: "app"));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadInstall(context.Parameters);
        var taskId = Guid.NewGuid().ToString();
        var body = BuildBody(arguments, taskId);

        var result = await context.Client
            .PostAsync<JsonElement>(AppStoreRoutes.Install, body, cancellationToken)
            .ConfigureAwait(false);

        var detail = new JsonObject
        {
            ["appDetailId"] = arguments.AppDetailId,
            ["name"] = arguments.Name,
            ["appKey"] = arguments.AppKey,
            ["version"] = arguments.Version,
            ["taskId"] = taskId,
            ["parameterKeys"] = ToArray(AppStoreOperationSupport.ParameterKeys(arguments.Parameters))
        };

        return AppStoreOperationSupport.FromCall(
            context,
            result,
            $"安装任务已提交（{arguments.Name}，任务号 {taskId}），请在“已安装应用”中刷新查看状态。",
            taskId,
            detail.ToJsonString(),
            arguments.Name);
    }

    private static Dictionary<string, object?> BuildBody(AppInstallArguments arguments, string taskId)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["appDetailId"] = arguments.AppDetailId,
            ["name"] = arguments.Name,
            ["params"] = arguments.Parameters,
            ["advanced"] = true,
            ["cpuQuota"] = arguments.CpuQuota,
            ["memoryLimit"] = arguments.MemoryLimit,
            ["memoryUnit"] = arguments.MemoryUnit,
            ["containerName"] = arguments.ContainerName ?? string.Empty,
            ["allowPort"] = arguments.AllowPort,
            ["editCompose"] = arguments.EditCompose,
            ["dockerCompose"] = arguments.DockerCompose ?? string.Empty,
            ["hostMode"] = false,
            ["pullImage"] = arguments.PullImage,
            ["gpuConfig"] = arguments.GpuConfig,
            ["webUI"] = arguments.WebUI ?? string.Empty,
            ["specifyIP"] = arguments.SpecifyIp ?? string.Empty,
            ["taskID"] = taskId
        };

        body["restartPolicy"] = arguments.RestartPolicy ?? "always";
        return body;
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }
}
