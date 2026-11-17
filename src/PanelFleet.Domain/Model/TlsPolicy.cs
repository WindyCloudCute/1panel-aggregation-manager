namespace PanelFleet.Domain.Model;

/// <summary>节点 TLS 校验策略。</summary>
public enum TlsPolicy
{
    /// <summary>严格校验证书与主机名（默认）。</summary>
    Strict = 0,

    /// <summary>接受自签/不受信任证书（需在 UI 明确提示风险）。</summary>
    AllowSelfSigned = 1
}
