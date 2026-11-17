using PanelFleet.Application.Models;
using PanelFleet.Client;

namespace PanelFleet.Application.Operations;

/// <summary>预览阶段的上下文：调用参数 + 全部目标节点。</summary>
public sealed record OperationPlanContext(
    OperationInvocation Invocation,
    IReadOnlyList<NodeRecord> Nodes,
    OperationParameters Parameters);

/// <summary>执行阶段的上下文：单个节点 + 已创建的 1Panel 客户端。</summary>
public sealed record OperationNodeContext(
    Guid OperationId,
    NodeRecord Node,
    IPanelClient Client,
    OperationParameters Parameters);

/// <summary>
/// 业务写操作处理器：每个具体操作（安装应用/停止容器/升级面板…）实现一个。
/// <para>Kind 使用点分命名，如 <c>app.install</c>、<c>container.stop</c>、<c>panel.update</c>。</para>
/// </summary>
public interface IOperationHandler
{
    string Kind { get; }

    /// <summary>校验参数并给出预览计划（不产生副作用）。</summary>
    Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken);

    /// <summary>在单个节点上执行操作。失败时返回 <c>Success=false</c> 或抛出异常（内核会转为失败结果）。</summary>
    Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken);
}

/// <summary>操作处理器注册表（启动时构建，重复 Kind 直接失败）。</summary>
public sealed class OperationHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IOperationHandler> _handlers;

    public OperationHandlerRegistry(IEnumerable<IOperationHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<string, IOperationHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler.Kind))
            {
                throw new InvalidOperationException($"操作处理器 {handler.GetType().Name} 未声明 Kind。");
            }

            if (!map.TryAdd(handler.Kind.Trim(), handler))
            {
                throw new InvalidOperationException($"重复的操作类型：{handler.Kind}。");
            }
        }

        _handlers = map;
    }

    public int Count => _handlers.Count;

    public IReadOnlyCollection<string> Kinds => _handlers.Keys.ToArray();

    public bool TryResolve(string kind, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IOperationHandler? handler) =>
        _handlers.TryGetValue(kind, out handler);

    public IOperationHandler Resolve(string kind) =>
        _handlers.TryGetValue(kind, out var handler) ? handler : throw new OperationNotSupportedException(kind);
}
