namespace PanelFleet.Domain.Model;

/// <summary>
/// 节点接入描述。
/// ApiKey 仅在内存与签名过程中短暂使用，<see cref="ToString"/> 已脱敏。
/// </summary>
public sealed record NodeDescriptor(
    NodeId Id,
    string Name,
    Uri BaseUri,
    string ApiKey,
    AuthMode AuthMode = AuthMode.Auto,
    TlsPolicy TlsPolicy = TlsPolicy.Strict,
    string? ApiKeyHint = null,
    string? Remark = null,
    IReadOnlyList<string>? Tags = null,
    TimeSpan ClockOffset = default)
{
    public override string ToString() =>
        $"Node({Id}, {Name}, {BaseUri}, auth={AuthMode}, tls={TlsPolicy})";
}
