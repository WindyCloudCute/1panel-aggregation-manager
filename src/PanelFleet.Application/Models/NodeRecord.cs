using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Models;

/// <summary>节点持久化记录（Application 层视图，不含明文密钥）。</summary>
public sealed record NodeRecord(
    NodeId Id,
    string Name,
    Uri BaseUrl,
    string? SecurityEntrance,
    string ApiKeyProtected,
    string ApiKeyHint,
    AuthMode AuthMode,
    AuthMode? DetectedAuthMode,
    double? ClockOffsetSeconds,
    TlsPolicy TlsPolicy,
    Guid? GroupId,
    IReadOnlyList<string> Tags,
    string? Remark,
    bool IsEnabled,
    string? PanelVersion,
    int Capabilities,
    NodeHealth Health,
    string? HealthMessage,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
