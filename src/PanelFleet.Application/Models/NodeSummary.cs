using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Models;

/// <summary>对外节点摘要，永不包含密文或明文密钥。</summary>
public sealed record NodeSummary(
    NodeId Id,
    string Name,
    Uri BaseUrl,
    string? SecurityEntrance,
    Guid? GroupId,
    string? GroupName,
    IReadOnlyList<string> Tags,
    string? Remark,
    string ApiKeyHint,
    AuthMode AuthMode,
    AuthMode? DetectedAuthMode,
    TlsPolicy TlsPolicy,
    string? PanelVersion,
    int Capabilities,
    NodeHealth Health,
    string? HealthMessage,
    DateTimeOffset? LastCheckedAt,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
