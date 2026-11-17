namespace PanelFleet.Web.Auth;

/// <summary>Pocket ID / 通用 OIDC 配置（ClientSecret 只来自环境变量或用户机密）。</summary>
public sealed class OidcOptions
{
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string CallbackPath { get; set; } = "/sso/OIDC/Callback";

    public string SignedOutCallbackPath { get; set; } = "/sso/OIDC/SignedOut";

    public string? RedirectUri { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public string RoleClaimType { get; set; } = "groups";

    public string[] AdminRoles { get; set; } = [];

    public string[] ReadOnlyRoles { get; set; } = [];

    public void Validate()
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority)
            || authority.Scheme is not ("http" or "https"))
        {
            errors.Add("Oidc:Authority 必须是绝对的 http/https 地址。");
        }
        else if (RequireHttpsMetadata && authority.Scheme != "https")
        {
            errors.Add("Oidc:Authority 在 RequireHttpsMetadata=true 时必须是 https。");
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            errors.Add("Oidc:ClientId 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            errors.Add("Oidc:ClientSecret 不能为空（请通过环境变量注入）。");
        }

        if (string.IsNullOrWhiteSpace(CallbackPath) || !CallbackPath.StartsWith('/'))
        {
            errors.Add("Oidc:CallbackPath 必须以 / 开头。");
        }

        if (!string.IsNullOrWhiteSpace(RedirectUri)
            && (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect)
                || redirect.Scheme is not ("http" or "https")))
        {
            errors.Add("Oidc:RedirectUri 必须是绝对的 http/https 地址。");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("OIDC 配置无效：" + string.Join(" | ", errors));
        }
    }
}
