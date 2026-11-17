using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>连接测试/能力探测结果。</summary>
public sealed record CapabilityProbeResult(
    bool Reachable,
    bool Authorized,
    string? PanelVersion,
    string? OsDescription,
    PanelError? Error,
    TimeSpan Elapsed);
