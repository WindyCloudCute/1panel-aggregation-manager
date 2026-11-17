namespace PanelFleet.Client;

/// <summary>
/// 1Panel v2.3.2 设置与认证相关路由（相对 BaseUri，不含前导斜杠）。
/// <para>core 前缀路由由 1Panel 主服务处理；agent 前缀路由（<c>api/v2/settings/*</c>）由节点 agent 处理。
/// 路由依据 1Panel 源码 tag <c>v2.3.2</c>：<c>core/router/ro_setting.go</c>、<c>core/router/ro_auth.go</c>、
/// <c>agent/router/ro_setting.go</c>。</para>
/// </summary>
public static class SettingsRoutes
{
    /// <summary>读取完整面板设置（POST，空 body <c>{}</c>）→ <c>PanelSettingInfoDto</c>。</summary>
    public const string CoreSettingsSearch = "api/v2/core/settings/search";

    /// <summary>读取基础面板设置（POST，空 body）→ <c>PanelSettingSearchBaseDto</c>；只需 SessionAuth。</summary>
    public const string CoreSettingsSearchBase = "api/v2/core/settings/search/base";

    /// <summary>读取终端设置（POST，空 body）→ <c>PanelTerminalInfoDto</c>。</summary>
    public const string CoreTerminalSearch = "api/v2/core/settings/terminal/search";

    /// <summary>更新单项设置（POST <c>{"key","value"}</c>）。key 必须在 1Panel 设置键白名单内。</summary>
    public const string CoreSettingsUpdate = "api/v2/core/settings/update";

    /// <summary>更新终端设置（POST <c>PanelTerminalUpdateRequest</c>）。</summary>
    public const string CoreTerminalUpdate = "api/v2/core/settings/terminal/update";

    /// <summary>读取 SSL 证书信息（GET）→ <c>PanelSslInfoDto</c>；无证书时 data 为 null。</summary>
    public const string CoreSslInfo = "api/v2/core/settings/ssl/info";

    /// <summary>读取当前登录用户信息（GET）→ <c>PanelCurrentUserDto</c>；含 MFA/API 接口开关状态。</summary>
    public const string CoreCurrentUser = "api/v2/core/auth/current";

    /// <summary>修改当前用户信息/密码（POST <c>PanelCurrentUserUpdateRequest</c>，需旧密码与新密码）。</summary>
    public const string CoreCurrentUserUpdate = "api/v2/core/auth/current/update";

    /// <summary>更新 API 接口开关与密钥配置（POST <c>PanelApiConfigUpdateRequest</c>，需 API Key 会话）。</summary>
    public const string CoreApiConfigUpdate = "api/v2/core/auth/api/update";

    /// <summary>读取节点 agent 信息（POST，空 body）→ <c>SettingsSearchDto</c>。</summary>
    public const string AgentSettingsSearch = "api/v2/settings/search";

    /// <summary>读取节点默认网站目录（GET）→ 响应 <c>{ "dir": "..." }</c> 形态由 agent 决定，按扩展数据读取。</summary>
    public const string AgentWebsiteDir = "api/v2/settings/website/dir";

    /// <summary>读取节点基础目录（GET）。</summary>
    public const string AgentBaseDir = "api/v2/settings/basedir";
}
