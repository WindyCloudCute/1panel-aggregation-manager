using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.AppStore;

/// <summary>应用卸载写操作（<c>app.uninstall</c>）：风险 High，批量 Critical；调用 <c>POST apps/installed/op</c>（operate=delete）。</summary>
public sealed class AppUninstallOperationHandler : IOperationHandler
{
    public string Kind => "app.uninstall";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadUninstall(context.Parameters);
        var display = arguments.Name ?? $"应用 #{AppStoreOperationSupport.FormatInstallId(arguments.InstallId)}";
        var risk = AppStoreOperationSupport.BatchRisk(context.Nodes.Count, OperationRisk.High);

        var warnings = new List<string>
        {
            "卸载会删除应用容器与编排文件，操作不可回滚。",
            "请确认应用数据已备份（是否需要删除备份/镜像由卸载选项决定）。"
        };

        if (arguments.DeleteDatabase)
        {
            warnings.Add("已勾选“同时删除关联数据库”，数据库数据将不可恢复！");
        }

        if (arguments.DeleteBackup)
        {
            warnings.Add("已勾选“删除备份”，历史备份将不可恢复。");
        }

        if (arguments.DeleteImage)
        {
            warnings.Add("已勾选“删除镜像”，之后重装需要重新拉取镜像。");
        }

        var summary =
            $"从 {context.Nodes.Count} 个节点卸载 {display}（安装 ID {arguments.InstallId}）"
            + $"：数据库删除={Flag(arguments.DeleteDatabase)}，备份删除={Flag(arguments.DeleteBackup)}，"
            + $"镜像删除={Flag(arguments.DeleteImage)}。";

        return Task.FromResult(new OperationPlan(
            $"卸载应用 {display}",
            summary,
            risk,
            AppStoreOperationSupport.Targets(context),
            warnings,
            TargetType: "app"));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadUninstall(context.Parameters);
        var taskId = Guid.NewGuid().ToString();

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operate"] = "delete",
            ["installId"] = arguments.InstallId,
            ["forceDelete"] = arguments.ForceDelete,
            ["deleteBackup"] = arguments.DeleteBackup,
            ["deleteDB"] = arguments.DeleteDatabase,
            ["deleteImage"] = arguments.DeleteImage,
            ["taskID"] = taskId
        };

        var result = await context.Client
            .PostAsync<JsonElement>(AppStoreRoutes.InstalledOperate, body, cancellationToken)
            .ConfigureAwait(false);

        var targetId = AppStoreOperationSupport.FormatInstallId(arguments.InstallId);
        var detail = new JsonObject
        {
            ["installId"] = arguments.InstallId,
            ["deleteDB"] = arguments.DeleteDatabase,
            ["deleteBackup"] = arguments.DeleteBackup,
            ["deleteImage"] = arguments.DeleteImage,
            ["taskId"] = taskId
        };

        return AppStoreOperationSupport.FromCall(
            context,
            result,
            $"卸载任务已提交（安装 ID {arguments.InstallId}，任务号 {taskId}），容器/数据删除不可恢复。",
            taskId,
            detail.ToJsonString(),
            targetId);
    }

    private static string Flag(bool value) => value ? "是" : "否";
}
