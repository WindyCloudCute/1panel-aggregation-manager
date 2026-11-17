using System.Text.Json;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.AppStore;

/// <summary>应用商店写操作处理器的公共逻辑（目标、风险、结果映射）。</summary>
internal static class AppStoreOperationSupport
{
    /// <summary>批量（&gt;1 节点）统一提升为 Critical，与内核策略一致。</summary>
    public static OperationRisk BatchRisk(int nodeCount, OperationRisk singleNodeRisk) =>
        nodeCount > 1 ? OperationRisk.Critical : singleNodeRisk;

    public static OperationTarget[] Targets(OperationPlanContext context) =>
        context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray();

    /// <summary>把 1Panel HTTP 调用结果转为节点执行结果；<paramref name="taskId"/> 为 1Panel 异步任务号。</summary>
    public static OperationStepResult FromCall(
        OperationNodeContext context,
        PanelCallResult<JsonElement> result,
        string successMessage,
        string? taskId,
        string? detailJson,
        string? targetId)
    {
        if (!result.IsSuccess)
        {
            var error = result.Error ?? new PanelError(PanelErrorKind.Protocol, "1Panel 未返回错误信息。");
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                error.Message,
                taskId,
                detailJson,
                error,
                TimeSpan.Zero,
                targetId);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            successMessage,
            taskId,
            detailJson,
            null,
            TimeSpan.Zero,
            targetId);
    }

    public static string FormatInstallId(long installId) => installId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>记录参数键名（不记录值），供结果与审计展示。</summary>
    public static string[] ParameterKeys(System.Text.Json.Nodes.JsonObject? parameters) =>
        parameters is null ? [] : parameters.Select(pair => pair.Key).ToArray();
}
