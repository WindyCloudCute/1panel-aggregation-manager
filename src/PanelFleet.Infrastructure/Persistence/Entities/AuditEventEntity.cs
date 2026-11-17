namespace PanelFleet.Infrastructure.Persistence.Entities;

public sealed class AuditEventEntity
{
    public string Id { get; set; } = string.Empty;

    public string OccurredAt { get; set; } = string.Empty;

    public string? ActorSubject { get; set; }

    public string? ActorName { get; set; }

    public string? ActorEmail { get; set; }

    public string Action { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    public string? TargetId { get; set; }

    public string? NodeId { get; set; }

    public string? DetailJson { get; set; }

    public string? ClientIp { get; set; }

    public string? TraceId { get; set; }
}
