namespace PanelFleet.Application.Terminal;

/// <summary>Origin 校验结论。</summary>
public sealed record TerminalOriginDecision(bool Allowed, string? Reason)
{
    public static TerminalOriginDecision Allow { get; } = new(true, null);

    public static TerminalOriginDecision Reject(string reason) => new(false, reason);
}

/// <summary>
/// WebSocket Origin 校验（防跨站 WebSocket 劫持）。
/// 浏览器 WebSocket 不受同源策略限制，必须由服务端校验 Origin：
/// <list type="bullet">
///   <item>带 Origin：host+port（+scheme 默认端口）必须与请求 Host 同源，或在白名单中；</item>
///   <item>不带 Origin：仅当 <c>AllowMissingOrigin=true</c> 时放行（非浏览器客户端，仍需 Admin 鉴权）；</item>
///   <item><c>Origin: null</c> 一律拒绝。</item>
/// </list>
/// 纯函数，便于单元测试。
/// </summary>
public static class TerminalOriginPolicy
{
    public static TerminalOriginDecision Evaluate(
        string? origin,
        string host,
        string scheme,
        bool allowMissingOrigin,
        IReadOnlyCollection<string>? allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return allowMissingOrigin
                ? TerminalOriginDecision.Allow
                : TerminalOriginDecision.Reject("请求缺少 Origin 头，已按配置拒绝。");
        }

        if (string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return TerminalOriginDecision.Reject("Origin 为 null，已拒绝。");
        }

        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var originUri)
            || (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps))
        {
            return TerminalOriginDecision.Reject("Origin 格式非法，已拒绝。");
        }

        if (IsAllowedOrigin(originUri, allowedOrigins))
        {
            return TerminalOriginDecision.Allow;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return TerminalOriginDecision.Reject("请求缺少 Host 头，无法校验 Origin。");
        }

        var requestScheme = string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttps
            : Uri.UriSchemeHttp;

        if (!Uri.TryCreate($"{requestScheme}://{host.Trim()}", UriKind.Absolute, out var requestUri))
        {
            return TerminalOriginDecision.Reject("Host 头格式非法，无法校验 Origin。");
        }

        if (!string.Equals(originUri.Host, requestUri.Host, StringComparison.OrdinalIgnoreCase)
            || GetEffectivePort(originUri) != GetEffectivePort(requestUri))
        {
            return TerminalOriginDecision.Reject("Origin 与请求 Host 不一致，已拒绝。");
        }

        return TerminalOriginDecision.Allow;
    }

    private static bool IsAllowedOrigin(Uri originUri, IReadOnlyCollection<string>? allowedOrigins)
    {
        if (allowedOrigins is null || allowedOrigins.Count == 0)
        {
            return false;
        }

        foreach (var candidate in allowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var allowed)
                && string.Equals(allowed.Host, originUri.Host, StringComparison.OrdinalIgnoreCase)
                && GetEffectivePort(allowed) == GetEffectivePort(originUri)
                && string.Equals(allowed.Scheme, originUri.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetEffectivePort(Uri uri) =>
        uri.IsDefaultPort ? DefaultPort(uri.Scheme) : uri.Port;

    private static int DefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
}
