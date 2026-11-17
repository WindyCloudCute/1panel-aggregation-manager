using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Operations;

public interface IWriteOperationService
{
    /// <summary>生成预览与一次性确认 Token（无副作用）。</summary>
    Task<OperationPreview> PreviewAsync(OperationInvocation invocation, ActorInfo actor, CancellationToken cancellationToken);

    /// <summary>凭确认 Token 执行写操作：逐节点顺序执行、失败停止、逐节点审计。</summary>
    Task<OperationExecution> ExecuteAsync(Guid tokenId, string? typedConfirmation, ActorInfo actor, CancellationToken cancellationToken);

    OperationExecution? GetExecution(Guid operationId);

    IReadOnlyList<OperationExecution> ListRecent(int take);
}

/// <summary>
/// 写操作内核：预览 → 强确认 → 逐节点执行 → 审计。
/// <para>安全约束：不做自动重试、不回滚、确认 Token 一次性、批量失败即停止。</para>
/// </summary>
public sealed class WriteOperationService : IWriteOperationService
{
    private readonly INodeStore _nodeStore;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly OperationHandlerRegistry _handlers;
    private readonly IConfirmationStore _confirmations;
    private readonly IOperationHistoryStore _history;
    private readonly OperationGuards _guards;
    private readonly IAuditStore _audit;
    private readonly OperationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WriteOperationService> _logger;

    public WriteOperationService(
        INodeStore nodeStore,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        OperationHandlerRegistry handlers,
        IConfirmationStore confirmations,
        IOperationHistoryStore history,
        OperationGuards guards,
        IAuditStore audit,
        OperationOptions options,
        TimeProvider timeProvider,
        ILogger<WriteOperationService> logger)
    {
        _nodeStore = nodeStore;
        _protector = protector;
        _clientFactory = clientFactory;
        _handlers = handlers;
        _confirmations = confirmations;
        _history = history;
        _guards = guards;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<OperationPreview> PreviewAsync(
        OperationInvocation invocation,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(actor);

        if (string.IsNullOrWhiteSpace(invocation.Kind))
        {
            throw new OperationValidationException("操作类型不能为空。");
        }

        var handler = _handlers.Resolve(invocation.Kind.Trim());
        var nodes = await ResolveNodesAsync(invocation.NodeIds, cancellationToken).ConfigureAwait(false);
        var parameters = OperationParameters.From(invocation.Parameters);

        // 预览阶段捕获参数校验异常，直接返回给调用方（未生成 Token、无副作用）。
        var plan = await handler
            .PlanAsync(new OperationPlanContext(invocation, nodes, parameters), cancellationToken)
            .ConfigureAwait(false);

        var targets = plan.Targets is { Count: > 0 }
            ? plan.Targets
            : nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray();

        var requiresTypedConfirmation = plan.Risk >= OperationRisk.High;
        var confirmText = requiresTypedConfirmation
            ? plan.TypedConfirmationText ?? (nodes.Count == 1 ? nodes[0].Name : $"{nodes.Count} 个节点")
            : null;

        var now = _timeProvider.GetUtcNow();
        var tokenId = Guid.NewGuid();
        var preview = new OperationPreview(
            tokenId,
            handler.Kind,
            plan.Title,
            plan.Summary,
            plan.Risk,
            requiresTypedConfirmation,
            confirmText,
            targets,
            plan.Warnings,
            now,
            now.AddSeconds(_options.ConfirmationTtlSeconds));

        _confirmations.Create(invocation, plan, ActorKey(actor), preview, preview.ExpiresAt);
        return preview;
    }

    public async Task<OperationExecution> ExecuteAsync(
        Guid tokenId,
        string? typedConfirmation,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var actorKey = ActorKey(actor);
        var now = _timeProvider.GetUtcNow();
        var confirmation = _confirmations.TryConsume(tokenId, actorKey, now);
        if (confirmation is null)
        {
            await WriteAuditAsync(CreateRejectionAudit(actor, "unknown", "expired", "确认已过期、已使用或不属于当前用户。", null))
                .ConfigureAwait(false);
            throw new OperationConfirmationException("确认已过期或已被使用，请重新预览后再确认。", "expired");
        }

        var preview = confirmation.Preview;
        var handler = _handlers.Resolve(preview.Kind);

        if (preview.RequiresTypedConfirmation
            && !string.Equals((typedConfirmation ?? string.Empty).Trim(), preview.ConfirmText, StringComparison.Ordinal))
        {
            await WriteAuditAsync(
                    CreateRejectionAudit(
                        actor,
                        handler.Kind,
                        "typed_confirmation_mismatch",
                        "确认文本不匹配。",
                        preview))
                .ConfigureAwait(false);
            throw new OperationConfirmationException($"确认文本不匹配，请输入：{preview.ConfirmText}", "typed_confirmation_mismatch");
        }

        var fingerprint = BuildFingerprint(confirmation.Invocation);
        if (_guards.IsDuplicate(fingerprint, now, TimeSpan.FromSeconds(_options.DedupeWindowSeconds)))
        {
            await WriteAuditAsync(CreateRejectionAudit(actor, handler.Kind, "duplicate", "相同操作在去重窗口内重复提交。", preview))
                .ConfigureAwait(false);
            throw new OperationConfirmationException("相同操作刚刚已提交，请勿重复执行。", "duplicate");
        }

        IReadOnlyList<NodeRecord> nodes;
        try
        {
            nodes = await ResolveNodesAsync(confirmation.Invocation.NodeIds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationValidationException exception)
        {
            await WriteAuditAsync(CreateRejectionAudit(actor, handler.Kind, "invalid_nodes", exception.Message, preview))
                .ConfigureAwait(false);
            throw;
        }

        var operationId = Guid.NewGuid();
        var startedAt = _timeProvider.GetUtcNow();
        var parameters = OperationParameters.From(confirmation.Invocation.Parameters);
        var executeTimeout = TimeSpan.FromSeconds(_options.ExecuteTimeoutSeconds);
        var lockTimeout = TimeSpan.FromSeconds(_options.NodeLockTimeoutSeconds);
        var steps = new List<OperationStepResult>(nodes.Count);

        foreach (var node in nodes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                steps.Add(Cancelled(node, "操作已取消，未执行。"));
                break;
            }

            using var lease = await _guards.TryAcquireNodeAsync(node.Id, lockTimeout, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                steps.Add(Cancelled(node, $"节点 {node.Name} 正在执行其他写操作，已停止批次。"));
                break;
            }

            var nodeStopwatch = Stopwatch.StartNew();
            var step = await ExecuteOnNodeAsync(
                    handler,
                    operationId,
                    node,
                    parameters,
                    executeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            steps.Add(step);

            await WriteAuditAsync(
                    CreateStepAudit(actor, handler.Kind, confirmation.Plan, node, step, operationId, step.Success, parameters))
                .ConfigureAwait(false);

            if (!step.Success)
            {
                break;
            }
        }

        var succeeded = steps.Count(step => step.Success);
        var status = succeeded == nodes.Count
            ? OperationStatus.Succeeded
            : succeeded > 0
                ? OperationStatus.PartiallySucceeded
                : OperationStatus.Failed;

        var finishedAt = _timeProvider.GetUtcNow();
        var execution = new OperationExecution(
            operationId,
            handler.Kind,
            confirmation.Plan.Title,
            confirmation.Plan.Risk,
            status,
            startedAt,
            finishedAt,
            steps);

        _history.Add(execution);
        _guards.MarkExecuted(fingerprint, finishedAt);

        if (nodes.Count > 1)
        {
            await WriteAuditAsync(CreateBatchAudit(actor, handler.Kind, confirmation.Plan, execution, nodes.Count))
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "写操作完成 {Kind} 操作号 {OperationId} 节点数 {NodeCount} 状态 {Status}",
            handler.Kind,
            operationId,
            nodes.Count,
            status);

        return execution;
    }

    public OperationExecution? GetExecution(Guid operationId) => _history.TryGet(operationId);

    public IReadOnlyList<OperationExecution> ListRecent(int take) => _history.ListRecent(take);

    private async Task<OperationStepResult> ExecuteOnNodeAsync(
        IOperationHandler handler,
        Guid operationId,
        NodeRecord node,
        OperationParameters parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = _clientFactory.Create(BuildDescriptor(node));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var context = new OperationNodeContext(operationId, node, client, parameters);
            var step = await handler.ExecuteAsync(context, timeoutCts.Token).ConfigureAwait(false);
            return step with { Duration = stopwatch.Elapsed };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(node, "操作已取消。") with { Duration = stopwatch.Elapsed };
        }
        catch (OperationCanceledException)
        {
            return new OperationStepResult(
                node.Id,
                node.Name,
                false,
                $"执行超时（超过 {_options.ExecuteTimeoutSeconds} 秒）。",
                null,
                null,
                null,
                stopwatch.Elapsed);
        }
        catch (OperationValidationException exception)
        {
            return new OperationStepResult(node.Id, node.Name, false, exception.Message, null, null, null, stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "写操作执行异常 {Kind} 节点 {NodeId}", handler.Kind, node.Id);
            return new OperationStepResult(
                node.Id,
                node.Name,
                false,
                "执行异常：" + Truncate(exception.Message, 300),
                null,
                null,
                null,
                stopwatch.Elapsed);
        }
    }

    private async Task<IReadOnlyList<NodeRecord>> ResolveNodesAsync(
        IReadOnlyList<NodeId> nodeIds,
        CancellationToken cancellationToken)
    {
        var distinct = nodeIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            throw new OperationValidationException("至少选择一个节点。");
        }

        if (distinct.Length > _options.MaxBatchNodes)
        {
            throw new OperationValidationException($"单次操作最多 {_options.MaxBatchNodes} 个节点。");
        }

        var nodes = new List<NodeRecord>(distinct.Length);
        foreach (var id in distinct)
        {
            var node = await _nodeStore.FindAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new OperationValidationException($"节点不存在或已被删除（{id.Value}）。");

            if (!node.IsEnabled)
            {
                throw new OperationValidationException($"节点 {node.Name} 已禁用，不允许执行写操作。");
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    private async Task WriteAuditAsync(AuditEntry entry)
    {
        try
        {
            await _audit.AddAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "写操作审计落库失败 {Action}", entry.Action);
        }
    }

    private AuditEntry CreateStepAudit(
        ActorInfo actor,
        string kind,
        OperationPlan plan,
        NodeRecord node,
        OperationStepResult step,
        Guid operationId,
        bool success,
        OperationParameters parameters)
    {
        var detail = CreateDetail(operationId, kind, plan);
        detail["nodeName"] = node.Name;
        detail["targetId"] = step.TargetId;
        detail["success"] = success;
        detail["message"] = Truncate(step.Message, 500);
        detail["taskId"] = step.TaskId;
        detail["durationMs"] = (long)step.Duration.TotalMilliseconds;
        detail["parameterKeys"] = new JsonArray(
            parameters.Keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
        detail["handlerDetail"] = Truncate(step.DetailJson, 500);

        return new AuditEntry(
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            actor,
            success ? "operation.executed" : "operation.failed",
            plan.TargetType ?? "node",
            step.TargetId ?? node.Id.Value.ToString(),
            node.Id,
            detail.ToJsonString());
    }

    private AuditEntry CreateBatchAudit(
        ActorInfo actor,
        string kind,
        OperationPlan plan,
        OperationExecution execution,
        int nodeCount)
    {
        var detail = CreateDetail(execution.Id, kind, plan);
        detail["nodeCount"] = nodeCount;
        detail["succeeded"] = execution.Steps.Count(step => step.Success);
        detail["failed"] = execution.Steps.Count(step => !step.Success);
        detail["status"] = execution.Status.ToString();

        return new AuditEntry(
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            actor,
            "operation.batch",
            plan.TargetType ?? "operation",
            execution.Id.ToString(),
            null,
            detail.ToJsonString());
    }

    private AuditEntry CreateRejectionAudit(
        ActorInfo actor,
        string kind,
        string reason,
        string message,
        OperationPreview? preview)
    {
        var detail = new JsonObject
        {
            ["reason"] = reason,
            ["kind"] = kind,
            ["message"] = Truncate(message, 300),
            ["tokenId"] = preview?.TokenId.ToString()
        };

        return new AuditEntry(
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            actor,
            "operation.rejected",
            "operation",
            preview?.TokenId.ToString(),
            null,
            detail.ToJsonString());
    }

    private static JsonObject CreateDetail(Guid operationId, string kind, OperationPlan plan) => new()
    {
        ["operationId"] = operationId.ToString(),
        ["kind"] = kind,
        ["title"] = plan.Title,
        ["risk"] = plan.Risk.ToString()
    };

    private static OperationStepResult Cancelled(NodeRecord node, string message) =>
        new(node.Id, node.Name, false, message, null, null, null, TimeSpan.Zero);

    private static string ActorKey(ActorInfo actor) => actor.Subject ?? actor.Name ?? "anonymous";

    private static string BuildFingerprint(OperationInvocation invocation)
    {
        var nodes = string.Join(
            ",",
            invocation.NodeIds
                .Select(id => id.Value.ToString("N"))
                .OrderBy(value => value, StringComparer.Ordinal));
        var parameters = invocation.Parameters?.ToJsonString() ?? "{}";
        var raw = invocation.Kind.Trim().ToLowerInvariant() + "|" + nodes + "|" + parameters;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

}
