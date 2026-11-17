using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Models;

public sealed record CreateNodeRequest(
    string? Name,
    string? BaseUrl,
    string? ApiKey,
    AuthMode AuthMode = AuthMode.Auto,
    TlsPolicy TlsPolicy = TlsPolicy.Strict,
    Guid? GroupId = null,
    IReadOnlyList<string>? Tags = null,
    string? Remark = null,
    bool IsEnabled = true,
    string? SecurityEntrance = null);

public sealed record UpdateNodeRequest(
    string? Name,
    string? BaseUrl,
    string? ApiKey,
    AuthMode AuthMode = AuthMode.Auto,
    TlsPolicy TlsPolicy = TlsPolicy.Strict,
    Guid? GroupId = null,
    IReadOnlyList<string>? Tags = null,
    string? Remark = null,
    bool IsEnabled = true,
    string? SecurityEntrance = null);

public sealed record TestDraftRequest(
    string? BaseUrl,
    string? ApiKey,
    AuthMode AuthMode = AuthMode.Auto,
    TlsPolicy TlsPolicy = TlsPolicy.Strict);

public sealed record NodeConnectionInput(
    string Name,
    Uri BaseUrl,
    string ApiKey,
    AuthMode AuthMode,
    TlsPolicy TlsPolicy,
    TimeSpan ClockOffset);

public sealed record NodeTestResult(
    bool Reachable,
    bool Authorized,
    long LatencyMs,
    AuthMode? DetectedAuthMode,
    double? ClockOffsetSeconds,
    string? PanelVersion,
    string? Os,
    PanelErrorKind? ErrorKind,
    string? ErrorMessage);

public sealed record NodeGroupSummary(Guid Id, string Name, int SortOrder);

public sealed record GroupCreateRequest(string? Name);

public sealed record GroupUpdateRequest(string? Name, int SortOrder);
