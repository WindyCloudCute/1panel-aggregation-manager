namespace PanelFleet.Domain.Model;

/// <summary>1Panel API 鉴权方式。</summary>
public enum AuthMode
{
    /// <summary>自动：优先 HMAC-SHA256，鉴权失败时回退 MD5。</summary>
    Auto = 0,

    /// <summary>HMAC-SHA256（推荐，1Panel v2.2.1+）。</summary>
    HmacSha256 = 1,

    /// <summary>MD5（兼容旧版本）。</summary>
    Md5 = 2
}
