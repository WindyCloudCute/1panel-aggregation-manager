using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.AppStore;

/// <summary>应用配置修改写操作（<c>app.params_update</c>）：风险 Medium，批量 Critical；调用 <c>POST apps/installed/params/update</c>。</summary>
public sealed class AppParamsUpdateOperationHandler : IOperationHandler
{
    public string Kind => "app.params_update";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadParamsUpdate(context.Parameters);
        var display = arguments.Name ?? $"应用 #{AppStoreOperationSupport.FormatInstallId(arguments.InstallId)}";
        var risk = AppStoreOperationSupport.BatchRisk(context.Nodes.Count, OperationRisk.Medium);
        var keys = AppStoreOperationSupport.ParameterKeys(arguments.Parameters);
        var keyText = keys.Length == 0 ? "无" : string.Join("、", keys);

        var warnings = new List<string>
        {
            "修改参数会重建/重启应用容器，可能短暂中断服务。",
            "端口、密码等参数变更后，需要同步更新使用该应用的客户端配置。",
            "不支持自动回滚；请确认参数值正确后再执行。"
        };

        if (arguments.Advanced)
        {
            warnings.Add("本次包含容器高级配置（CPU/内存/重启策略/编排）修改。");
        }

        var summary =
            $"在 {context.Nodes.Count} 个节点修改 {display}（安装 ID {arguments.InstallId}）的配置"
            + $"：参数键 {keyText}；高级配置={Flag(arguments.Advanced)}。";

        return Task.FromResult(new OperationPlan(
            $"修改应用配置 {display}",
            summary,
            risk,
            AppStoreOperationSupport.Targets(context),
            warnings,
            TargetType: "app"));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadParamsUpdate(context.Parameters);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["installId"] = arguments.InstallId,
            ["params"] = arguments.Parameters,
            ["advanced"] = arguments.Advanced
        };

        if (arguments.Advanced)
        {
            body["cpuQuota"] = arguments.CpuQuota ?? 0;
            body["memoryLimit"] = arguments.MemoryLimit ?? 0;
            body["memoryUnit"] = arguments.MemoryUnit ?? "M";
            body["containerName"] = arguments.ContainerName ?? string.Empty;
            body["allowPort"] = arguments.AllowPort ?? false;
            body["specifyIP"] = arguments.SpecifyIp ?? string.Empty;
            body["restartPolicy"] = arguments.RestartPolicy ?? "always";
            body["editCompose"] = arguments.EditCompose ?? false;
            body["dockerCompose"] = arguments.DockerCompose ?? string.Empty;
        }

        var result = await context.Client
            .PostAsync<JsonElement>(AppStoreRoutes.InstalledParamsUpdate, body, cancellationToken)
            .ConfigureAwait(false);

        var targetId = AppStoreOperationSupport.FormatInstallId(arguments.InstallId);
        var detail = new JsonObject
        {
            ["installId"] = arguments.InstallId,
            ["advanced"] = arguments.Advanced,
            ["parameterKeys"] = ToArray(AppStoreOperationSupport.ParameterKeys(arguments.Parameters))
        };

        return AppStoreOperationSupport.FromCall(
            context,
            result,
            $"配置修改已提交（安装 ID {arguments.InstallId}）。1Panel 会异步重建容器，请稍后刷新已安装列表确认状态。",
            null,
            detail.ToJsonString(),
            targetId);
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

    private static string Flag(bool value) => value ? "是" : "否";
}
