namespace PanelFleet.Infrastructure.Persistence.Entities;

public sealed class NodeEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string? SecurityEntrance { get; set; }

    public string ApiKeyProtected { get; set; } = string.Empty;

    public string ApiKeyHint { get; set; } = string.Empty;

    public int AuthMode { get; set; }

    public int? DetectedAuthMode { get; set; }

    public double? ClockOffsetSeconds { get; set; }

    public int TlsPolicy { get; set; }

    public string? GroupId { get; set; }

    public string TagsJson { get; set; } = "[]";

    public string? Remark { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public string? PanelVersion { get; set; }

    public int Capabilities { get; set; }

    public int HealthStatus { get; set; }

    public string? HealthMessage { get; set; }

    public string? LastCheckedAt { get; set; }

    public string? LastSeenAt { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;
}
