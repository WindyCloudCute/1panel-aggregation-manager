using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.AppStore;

/// <summary>应用升级写操作（<c>app.update</c>）：单节点 High、批量 Critical；调用 <c>POST apps/installed/op</c>（operate=upgrade）。</summary>
public sealed class AppUpdateOperationHandler : IOperationHandler
{
    public string Kind => "app.update";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadUpdate(context.Parameters);
        var display = arguments.Name ?? $"应用 #{AppStoreOperationSupport.FormatInstallId(arguments.InstallId)}";
        var risk = context.Nodes.Count > 1 ? OperationRisk.Critical : OperationRisk.High;
        var versionText = string.IsNullOrWhiteSpace(arguments.Version) ? "目标版本" : $"版本 {arguments.Version}";

        var warnings = new List<string>
        {
            "升级会重建应用容器，可能短暂中断服务。",
            arguments.Backup
                ? "升级前将自动备份应用数据。"
                : "未勾选升级前备份，升级失败可能无法恢复数据。",
            "不支持自动回滚；升级后请检查应用状态与业务可用性。"
        };

        if (!string.IsNullOrWhiteSpace(arguments.DockerCompose))
        {
            warnings.Add("已提供自定义 docker-compose，将覆盖目标版本编排文件。");
        }

        var summary =
            $"在 {context.Nodes.Count} 个节点将 {display}（安装 ID {arguments.InstallId}）升级到 {versionText}"
            + $"：备份={Flag(arguments.Backup)}，拉取镜像={Flag(arguments.PullImage)}，删除旧镜像={Flag(arguments.DeleteImage)}。";

        return Task.FromResult(new OperationPlan(
            $"升级应用 {display}",
            summary,
            risk,
            AppStoreOperationSupport.Targets(context),
            warnings,
            TargetType: "app"));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadUpdate(context.Parameters);
        var taskId = Guid.NewGuid().ToString();

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operate"] = "upgrade",
            ["installId"] = arguments.InstallId,
            ["detailId"] = arguments.DetailId,
            ["backup"] = arguments.Backup,
            ["pullImage"] = arguments.PullImage,
            ["deleteImage"] = arguments.DeleteImage,
            ["taskID"] = taskId
        };

        if (!string.IsNullOrWhiteSpace(arguments.DockerCompose))
        {
            body["dockerCompose"] = arguments.DockerCompose;
        }

        var result = await context.Client
            .PostAsync<JsonElement>(AppStoreRoutes.InstalledOperate, body, cancellationToken)
            .ConfigureAwait(false);

        var targetId = AppStoreOperationSupport.FormatInstallId(arguments.InstallId);
        var detail = new JsonObject
        {
            ["installId"] = arguments.InstallId,
            ["detailId"] = arguments.DetailId,
            ["version"] = arguments.Version,
            ["taskId"] = taskId
        };

        return AppStoreOperationSupport.FromCall(
            context,
            result,
            $"升级任务已提交（安装 ID {arguments.InstallId}，目标版本 {arguments.Version ?? arguments.DetailId.ToString()}，任务号 {taskId}）。",
            taskId,
            detail.ToJsonString(),
            targetId);
    }

    private static string Flag(bool value) => value ? "是" : "否";
}
