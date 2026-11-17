using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.AppStore;

/// <summary>
/// 跨节点批量升级应用（<c>app.batch_upgrade</c>）：批量 Critical、单应用 High。
/// 1Panel 没有批量升级接口，这里按节点、按应用顺序串行调用 <c>POST apps/installed/op</c>（operate=upgrade）；
/// 同一个节点内任一应用升级失败会立即返回失败，由内核停止后续节点，不自动重试、不回滚。
/// </summary>
public sealed class AppBatchUpgradeOperationHandler : IOperationHandler
{
    public string Kind => "app.batch_upgrade";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadBatchUpgrade(context.Parameters);

        var allowedNodes = context.Nodes.Select(node => node.Id.Value).ToHashSet();
        var unknownNode = arguments.Targets.FirstOrDefault(target => !allowedNodes.Contains(target.NodeId));
        if (unknownNode is not null)
        {
            throw new OperationValidationException(
                $"升级目标节点 {unknownNode.NodeId:D} 不在本次操作范围内。");
        }

        var distinctNodes = arguments.Targets.Select(target => target.NodeId).Distinct().Count();
        var distinctApps = arguments.Targets.Select(target => (target.NodeId, target.InstallId)).Distinct().Count();
        var risk = distinctApps > 1 || distinctNodes > 1 ? OperationRisk.Critical : OperationRisk.High;

        var nodeNames = context.Nodes.ToDictionary(node => node.Id.Value, node => node.Name);
        var summaryLines = arguments.Targets
            .GroupBy(target => target.NodeId)
            .Select(group =>
            {
                nodeNames.TryGetValue(group.Key, out var nodeName);
                var apps = string.Join(
                    "、",
                    group.Select(target => $"{Display(target)}（{target.Version}）"));
                return $"{nodeName ?? group.Key.ToString("D")}：{apps}";
            })
            .ToArray();

        var warnings = new List<string>
        {
            "升级会重建应用容器，可能短暂中断服务。",
            arguments.Backup
                ? "升级前将自动备份应用数据。"
                : "未勾选升级前备份，升级失败可能无法恢复数据。",
            "不支持自动回滚；升级后请检查应用状态与业务可用性。",
            "批量升级按节点顺序串行执行，任一应用失败会停止后续所有升级。"
        };

        var title = distinctApps > 1
            ? $"批量升级应用（{distinctApps} 个）"
            : $"升级应用 {Display(arguments.Targets[0])}";

        var summary =
            $"在 {distinctNodes} 个节点顺序升级 {distinctApps} 个应用（备份={Flag(arguments.Backup)}，"
            + $"拉取镜像={Flag(arguments.PullImage)}，删除旧镜像={Flag(arguments.DeleteImage)}）：\n"
            + string.Join("\n", summaryLines)
            + "\n批量升级按节点顺序串行执行，任一应用失败即停止，不自动重试、不回滚。";

        return Task.FromResult(new OperationPlan(
            title,
            summary,
            risk,
            AppStoreOperationSupport.Targets(context),
            warnings,
            TypedConfirmationText: $"升级 {distinctNodes} 个节点 {distinctApps} 个应用",
            TargetType: "app"));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = AppStoreOperationParameters.ReadBatchUpgrade(context.Parameters);
        var targets = arguments.Targets
            .Where(target => target.NodeId == context.Node.Id.Value)
            .ToArray();

        if (targets.Length == 0)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                "该节点没有待升级应用。",
                null,
                null,
                null,
                TimeSpan.Zero,
                context.Node.Id.Value.ToString("D"));
        }

        var taskIds = new List<string>(targets.Length);
        var apps = new JsonArray();
        var summaryParts = new List<string>(targets.Length);

        // 串行升级：同一节点内任一应用失败立即返回失败，避免后续应用在失败状态下继续执行。
        foreach (var target in targets)
        {
            var taskId = Guid.NewGuid().ToString();
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["operate"] = "upgrade",
                ["installId"] = target.InstallId,
                ["detailId"] = target.DetailId,
                ["backup"] = arguments.Backup,
                ["pullImage"] = arguments.PullImage,
                ["deleteImage"] = arguments.DeleteImage,
                ["taskID"] = taskId
            };

            if (!string.IsNullOrWhiteSpace(target.DockerCompose))
            {
                body["dockerCompose"] = target.DockerCompose;
            }

            var result = await context.Client
                .PostAsync<JsonElement>(AppStoreRoutes.InstalledOperate, body, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var error = result.Error;
                var message = $"升级 {Display(target)} 失败：{error?.Message ?? "未知错误"}";
                var detail = new JsonObject
                {
                    ["installId"] = target.InstallId,
                    ["version"] = target.Version,
                    ["detailId"] = target.DetailId,
                    ["taskId"] = taskId,
                    ["succeededInstallIds"] = new JsonArray(taskIds
                        .Select((id, index) => (JsonNode?)JsonValue.Create(targets[index].InstallId))
                        .ToArray())
                };

                return new OperationStepResult(
                    context.Node.Id,
                    context.Node.Name,
                    false,
                    message,
                    taskId,
                    detail.ToJsonString(),
                    error,
                    TimeSpan.Zero,
                    AppStoreOperationSupport.FormatInstallId(target.InstallId));
            }

            taskIds.Add(taskId);
            summaryParts.Add($"{Display(target)} {target.Version}");
            apps.Add(new JsonObject
            {
                ["installId"] = target.InstallId,
                ["version"] = target.Version,
                ["taskId"] = taskId
            });
        }

        var successMessage =
            $"在节点 {context.Node.Name} 已提交 {targets.Length} 个应用升级任务："
            + string.Join("，", summaryParts)
            + $"；任务号 {string.Join("，", taskIds)}。";

        var successDetail = new JsonObject
        {
            ["apps"] = apps,
            ["taskIds"] = new JsonArray(taskIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        };

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            successMessage,
            taskIds.Count > 0 ? taskIds[0] : null,
            successDetail.ToJsonString(),
            null,
            TimeSpan.Zero,
            context.Node.Id.Value.ToString("D"));
    }

    private static string Display(AppBatchUpgradeTarget target) =>
        string.IsNullOrWhiteSpace(target.Name)
            ? $"应用 #{AppStoreOperationSupport.FormatInstallId(target.InstallId)}"
            : target.Name!;

    private static string Flag(bool value) => value ? "是" : "否";
}
