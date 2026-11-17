namespace PanelFleet.Application.Models;

/// <summary>操作发起者信息（用于审计）。</summary>
public sealed record ActorInfo(
    string? Subject,
    string? Name,
    string? Email,
    string? ClientIp,
    string? TraceId)
{
    public static ActorInfo System { get; } = new("system", "System", null, null, null);
}
