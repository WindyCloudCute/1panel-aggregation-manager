using PanelFleet.Domain.Model;

namespace PanelFleet.Client.Signing;

/// <summary>单次请求的签名上下文。</summary>
internal sealed record SigningContext(
    string ApiKey,
    AuthMode Mode,
    TimeSpan ClockOffset,
    TimeProvider TimeProvider);
