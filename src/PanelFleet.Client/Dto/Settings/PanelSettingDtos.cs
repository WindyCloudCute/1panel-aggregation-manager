using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Settings;

/// <summary>
/// 1Panel <c>POST /api/v2/core/settings/search</c> 响应（<c>dto.SettingInfo</c>）。
/// <para>字段在 1Panel 中全部是字符串。敏感项 <c>proxyPasswd</c>/<c>proxyPasswdKeep</c> 故意不建模，
/// 保证明文密码不会进入 PanelFleet 进程内存中的强类型对象。</para>
/// </summary>
public sealed class PanelSettingInfoDto
{
    public string? PanelName { get; set; }

    public string? SystemVersion { get; set; }

    public string? Edition { get; set; }

    public string? Theme { get; set; }

    public string? Language { get; set; }

    public string? DeveloperMode { get; set; }

    public string? Port { get; set; }

    public string? ServerPort { get; set; }

    public string? Ipv6 { get; set; }

    public string? BindAddress { get; set; }

    public string? BindDomain { get; set; }

    public string? Ssl { get; set; }

    public string? SslType { get; set; }

    public string? SecurityEntrance { get; set; }

    public string? SessionTimeout { get; set; }

    public string? ExpirationDays { get; set; }

    public string? ComplexityVerification { get; set; }

    public string? MenuTabs { get; set; }

    public string? MenuAccordion { get; set; }

    public string? HideMenu { get; set; }

    public string? NoAuthSetting { get; set; }

    public string? DashboardMemoVisible { get; set; }

    public string? DashboardSimpleNodeVisible { get; set; }

    public string? AllowIps { get; set; }

    public string? AllowIpTrustedProxies { get; set; }

    public string? PasskeyTrustedProxies { get; set; }

    public string? DocSource { get; set; }

    public string? UpgradeBackupCopies { get; set; }

    public string? AppStoreVersion { get; set; }

    public string? AppStoreLastModified { get; set; }

    public string? AppStoreSyncStatus { get; set; }

    public string? ProxyUrl { get; set; }

    public string? ProxyType { get; set; }

    public string? ProxyPort { get; set; }

    public string? ProxyUser { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>1Panel <c>POST /api/v2/core/settings/search/base</c> 响应（<c>dto.SettingBaseInfo</c>）。</summary>
public sealed class PanelSettingSearchBaseDto
{
    public string? PanelName { get; set; }

    public string? SystemVersion { get; set; }

    public string? Edition { get; set; }

    public string? Theme { get; set; }

    public string? Language { get; set; }

    public string? DeveloperMode { get; set; }

    public string? ServerPort { get; set; }

    public string? Port { get; set; }

    public string? Ipv6 { get; set; }

    public string? BindAddress { get; set; }

    public string? SecurityEntrance { get; set; }

    public string? BindDomain { get; set; }

    public string? SessionTimeout { get; set; }

    public string? ExpirationDays { get; set; }

    public string? ComplexityVerification { get; set; }

    public string? NoAuthSetting { get; set; }

    public string? DocSource { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>1Panel <c>POST /api/v2/core/settings/terminal/search</c> 响应（<c>dto.TerminalInfo</c>）。</summary>
public sealed class PanelTerminalInfoDto
{
    public string? ShowTerminalButton { get; set; }

    public string? LineHeight { get; set; }

    public string? LetterSpacing { get; set; }

    public string? FontSize { get; set; }

    public string? FontFamily { get; set; }

    public string? BackgroundColor { get; set; }

    public string? ForegroundColor { get; set; }

    public string? CursorBlink { get; set; }

    public string? CursorStyle { get; set; }

    public string? Scrollback { get; set; }

    public string? ScrollSensitivity { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// 1Panel <c>GET /api/v2/core/settings/ssl/info</c> 响应（<c>dto.SSLInfo</c>）。
/// <para><see cref="Cert"/> 与 <see cref="Key"/> 是证书/私钥 PEM 原文，只在内存用于判断「是否已配置」，不对外下发。</para>
/// </summary>
public sealed class PanelSslInfoDto
{
    public string? Domain { get; set; }

    /// <summary>证书到期时间（1Panel 字段名即 timeout，字符串形式）。</summary>
    public string? Timeout { get; set; }

    public string? RootPath { get; set; }

    public string? Cert { get; set; }

    public string? Key { get; set; }

    public string? SslId { get; set; }

    /// <summary>是否已配置证书（取决于 cert/domain 是否为空）。</summary>
    [JsonIgnore]
    public bool HasCertificate => !string.IsNullOrWhiteSpace(Cert) || !string.IsNullOrWhiteSpace(Domain);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// 1Panel <c>GET /api/v2/core/auth/current</c> 响应（<c>dto.CurrentUserInfo</c>）。
/// <para><see cref="ApiKey"/> 是明文密钥，禁止下发；聚合层只读取「是否开启 API 接口」等状态字段。</para>
/// </summary>
public sealed class PanelCurrentUserDto
{
    public string? Name { get; set; }

    public string? MfaStatus { get; set; }

    public string? MfaInterval { get; set; }

    public string? ComplexitySetting { get; set; }

    public string? AuthSource { get; set; }

    public string? AuthSourceStatus { get; set; }

    public string? ApiInterfaceStatus { get; set; }

    public string? ApiKey { get; set; }

    public string? IpWhiteList { get; set; }

    public string? ApiTrustedProxies { get; set; }

    public string? ApiKeyValidityTime { get; set; }

    public string? Role { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>节点 agent <c>GET /api/v2/settings/website/dir</c> 响应（不同版本字段名略有差异，取第一个非空）。</summary>
public sealed class PanelWebsiteDirDto
{
    public string? Dir { get; set; }

    public string? WebsiteDir { get; set; }

    public string? Path { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>实际网站目录（顺序尝试 dir/websiteDir/path）。</summary>
    [JsonIgnore]
    public string? ResolvedDir =>
        !string.IsNullOrWhiteSpace(Dir) ? Dir
        : !string.IsNullOrWhiteSpace(WebsiteDir) ? WebsiteDir
        : !string.IsNullOrWhiteSpace(Path) ? Path
        : null;
}

/// <summary>1Panel 无 data 的确认响应（<c>data:null</c>）。</summary>
public sealed class PanelSettingsAckDto
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
