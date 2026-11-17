using System.Text.Json.Nodes;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Operations;

/// <summary>写操作风险等级。High/Critical 需要输入确认文本。</summary>
public enum OperationRisk
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

/// <summary>一次写操作（或批次）的整体状态。</summary>
public enum OperationStatus
{
    Succeeded = 0,
    Failed = 1,
    PartiallySucceeded = 2,
    Rejected = 3
}

/// <summary>写操作目标节点。</summary>
public sealed record OperationTarget(NodeId NodeId, string NodeName);

/// <summary>调用方提交的写操作请求：操作类型 + 目标节点 + 参数。</summary>
public sealed record OperationInvocation(
    string Kind,
    IReadOnlyList<NodeId> NodeIds,
    JsonObject? Parameters = null);

/// <summary>处理器在预览阶段给出的操作计划。</summary>
public sealed record OperationPlan(
    string Title,
    string Summary,
    OperationRisk Risk,
    IReadOnlyList<OperationTarget> Targets,
    IReadOnlyList<string> Warnings,
    string? TypedConfirmationText = null,
    string? TargetType = null);

/// <summary>返回给前端的确认弹窗数据（含一次性 Token）。</summary>
public sealed record OperationPreview(
    Guid TokenId,
    string Kind,
    string Title,
    string Summary,
    OperationRisk Risk,
    bool RequiresTypedConfirmation,
    string? ConfirmText,
    IReadOnlyList<OperationTarget> Targets,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>单个节点的执行结果。</summary>
public sealed record OperationStepResult(
    NodeId NodeId,
    string NodeName,
    bool Success,
    string Message,
    string? TaskId,
    string? DetailJson,
    PanelError? Error,
    TimeSpan Duration,
    string? TargetId = null);

/// <summary>一次写操作的执行结果（单节点或批量）。</summary>
public sealed record OperationExecution(
    Guid Id,
    string Kind,
    string Title,
    OperationRisk Risk,
    OperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<OperationStepResult> Steps);
