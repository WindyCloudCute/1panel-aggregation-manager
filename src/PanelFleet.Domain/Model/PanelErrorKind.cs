namespace PanelFleet.Domain.Model;

/// <summary>1Panel 调用失败的分类。</summary>
public enum PanelErrorKind
{
    /// <summary>401：API Key 错误、时间偏差或 IP 不在白名单。</summary>
    Unauthorized = 0,

    /// <summary>403：面板拒绝访问。</summary>
    Forbidden = 1,

    /// <summary>404：路径不存在或版本不支持。</summary>
    NotFound = 2,

    /// <summary>能力探测确认该版本不支持。</summary>
    Unsupported = 3,

    /// <summary>参数不合法（code 400）。</summary>
    Validation = 4,

    /// <summary>1Panel 内部错误（code 5xx）。</summary>
    PanelServerError = 5,

    /// <summary>非 JSON、信封缺失、重定向等协议问题。</summary>
    Protocol = 6,

    /// <summary>DNS、连接失败、连接重置。</summary>
    Network = 7,

    /// <summary>TLS 证书校验失败。</summary>
    Tls = 8,

    /// <summary>请求超时。</summary>
    Timeout = 9,

    /// <summary>调用方主动取消。</summary>
    Canceled = 10
}
