using System.Net;
using System.Security.Authentication;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.Errors;

/// <summary>将 HTTP 状态、1Panel 业务码与传输异常映射为 <see cref="PanelError"/>。</summary>
internal static class PanelErrorMapper
{
    public const int PanelSuccessCode = 200;

    public static PanelError FromHttpStatus(
        HttpStatusCode status,
        string? panelMessage,
        string? errorCode,
        string nodeName)
    {
        var kind = status switch
        {
            HttpStatusCode.Unauthorized => PanelErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => PanelErrorKind.Forbidden,
            HttpStatusCode.NotFound => PanelErrorKind.NotFound,
            HttpStatusCode.BadRequest => PanelErrorKind.Validation,
            _ when (int)status >= 500 => PanelErrorKind.PanelServerError,
            _ => PanelErrorKind.Protocol
        };

        return Create(kind, nodeName, panelMessage, errorCode, (int)status);
    }

    public static PanelError FromPanelCode(
        int code,
        string? panelMessage,
        string? errorCode,
        string nodeName)
    {
        var kind = code switch
        {
            401 => PanelErrorKind.Unauthorized,
            403 => PanelErrorKind.Forbidden,
            404 => PanelErrorKind.NotFound,
            400 or 422 => PanelErrorKind.Validation,
            >= 500 => PanelErrorKind.PanelServerError,
            _ => PanelErrorKind.Protocol
        };

        return Create(kind, nodeName, panelMessage, errorCode, httpStatus: null, panelCode: code);
    }

    public static PanelError MapTransport(Exception exception, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is HttpRequestException { InnerException: AuthenticationException })
        {
            return Create(PanelErrorKind.Tls, nodeName, detail: exception.Message, cause: exception);
        }

        return Create(PanelErrorKind.Network, nodeName, detail: exception.Message, cause: exception);
    }

    public static PanelError Create(
        PanelErrorKind kind,
        string nodeName,
        string? panelMessage = null,
        string? errorCode = null,
        int? httpStatus = null,
        int? panelCode = null,
        string? detail = null,
        Exception? cause = null)
    {
        var parts = new List<string> { $"[{nodeName}] {UserText(kind)}" };

        if (!string.IsNullOrWhiteSpace(detail))
        {
            parts.Add(detail);
        }

        if (!string.IsNullOrWhiteSpace(panelMessage))
        {
            parts.Add($"1Panel: {panelMessage}");
        }

        return new PanelError(
            kind,
            string.Join(" | ", parts),
            httpStatus,
            panelCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            errorCode,
            cause);
    }

    private static string UserText(PanelErrorKind kind) => kind switch
    {
        PanelErrorKind.Unauthorized => "API Key 错误、时间不同步或 IP 不在白名单。",
        PanelErrorKind.Forbidden => "面板拒绝访问（检查 API Key 的 IP 白名单）。",
        PanelErrorKind.NotFound => "接口不存在，可能 1Panel 版本不兼容。",
        PanelErrorKind.Unsupported => "该节点版本不支持此能力。",
        PanelErrorKind.Validation => "请求参数不合法。",
        PanelErrorKind.PanelServerError => "1Panel 内部错误。",
        PanelErrorKind.Protocol => "返回格式异常（检查反向代理/入口路径）。",
        PanelErrorKind.Network => "无法连接节点（网络/端口/防火墙）。",
        PanelErrorKind.Tls => "TLS 证书校验失败（自签证书请开启 AllowSelfSigned）。",
        PanelErrorKind.Timeout => "节点响应超时。",
        PanelErrorKind.Canceled => "已取消。",
        _ => "未知错误。"
    };
}
