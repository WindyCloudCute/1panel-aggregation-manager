using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Models;

/// <summary>审计事件（写入前由服务构造）。</summary>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    ActorInfo Actor,
    string Action,
    string TargetType,
    string? TargetId,
    NodeId? NodeId,
    string? DetailJson);
