using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Settings;
using PanelFleet.Domain;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Settings;

/// <summary>
/// 按节点读取 1Panel 面板设置（只读聚合）。所有下游调用相互独立、逐项容错：
/// 单个接口失败只产生一条 warning，不影响其它字段展示。
/// </summary>
public sealed class PanelSettingsService
{
    private readonly INodeStore _nodes;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly SettingsModuleOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PanelSettingsService> _logger;

    public PanelSettingsService(
        INodeStore nodes,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        SettingsModuleOptions options,
        TimeProvider time,
        ILogger<PanelSettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _nodes = nodes;
        _protector = protector;
        _clientFactory = clientFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>读取指定节点的面板设置。</summary>
    public async Task<PanelSettingsResult> GetAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var record = await _nodes.FindAsync(new NodeId(nodeId), cancellationToken);
        if (record is null)
        {
            return new PanelSettingsResult(false, null);
        }

        var warnings = new List<string>();
        var unsupported = new List<string>
        {
            "备用节点与灾备状态：1Panel 社区版 v2.3.2 未发现对应只读接口。"
        };

        if (!record.IsEnabled)
        {
            return new PanelSettingsResult(
                true,
                new PanelSettingsView(
                    nodeId,
                    record.Name,
                    record.BaseUrl.ToString(),
                    false,
                    _time.GetUtcNow(),
                    0,
                    "节点已禁用，未读取面板设置。",
                    [],
                    warnings,
                    unsupported));
        }

        var stopwatch = Stopwatch.StartNew();
        PanelSettingInfoDto? setting = null;
        PanelCurrentUserDto? currentUser = null;
        PanelTerminalInfoDto? terminal = null;
        PanelSslInfoDto? ssl = null;
        PanelWebsiteDirDto? websiteDir = null;

        using var client = _clientFactory.Create(BuildDescriptor(record));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.PanelRequestTimeoutSeconds));
        var token = timeoutCts.Token;

        async Task<T?> TryAsync<T>(string label, Func<Task<PanelCallResult<T>>> call)
            where T : class
        {
            try
            {
                var result = await call();
                if (result.Error is not null)
                {
                    warnings.Add($"{label}读取失败：{result.Error.Message}");
                    return null;
                }

                return result.Data;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"{label}读取超时（超过 {_options.PanelRequestTimeoutSeconds} 秒）。");
                return null;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "读取节点 {NodeName} 的{Label}失败", record.Name, label);
                warnings.Add($"{label}读取异常：{exception.Message}");
                return null;
            }
        }

        setting = await TryAsync("面板设置", () => client.PostAsync<PanelSettingInfoDto>(SettingsRoutes.CoreSettingsSearch, new { }, token));
        currentUser = await TryAsync("用户与 API 状态", () => client.GetAsync<PanelCurrentUserDto>(SettingsRoutes.CoreCurrentUser, token));
        terminal = await TryAsync("终端设置", () => client.PostAsync<PanelTerminalInfoDto>(SettingsRoutes.CoreTerminalSearch, new { }, token));
        ssl = await TryAsync("SSL 证书", () => client.GetAsync<PanelSslInfoDto>(SettingsRoutes.CoreSslInfo, token));
        websiteDir = await TryAsync("默认网站目录", () => client.GetAsync<PanelWebsiteDirDto>(SettingsRoutes.AgentWebsiteDir, token));

        stopwatch.Stop();

        var sections = new List<PanelSettingSection>();
        sections.Add(BuildPanelSection(setting, ssl, websiteDir));
        sections.Add(BuildSecuritySection(setting, currentUser));
        sections.Add(BuildApiSection(currentUser));
        sections.Add(BuildTerminalSection(terminal));

        string? error = setting is null && currentUser is null
            ? "面板设置读取失败，请检查节点状态与 API Key 权限。"
            : null;

        var view = new PanelSettingsView(
            nodeId,
            record.Name,
            record.BaseUrl.ToString(),
            true,
            _time.GetUtcNow(),
            stopwatch.ElapsedMilliseconds,
            error,
            sections,
            warnings,
            unsupported);

        return new PanelSettingsResult(true, view);
    }

    private static PanelSettingSection BuildPanelSection(
        PanelSettingInfoDto? setting,
        PanelSslInfoDto? ssl,
        PanelWebsiteDirDto? websiteDir)
    {
        const string readonlyHint = "需在 1Panel 面板操作；修改端口/监听地址/安全入口/SSL 会重启面板并断开 PanelFleet 连接。";

        return new PanelSettingSection(
            "panel",
            "面板信息",
            "1Panel 面板基础信息。端口、安全入口与 SSL 只读展示，请直接在 1Panel 面板修改。",
            ReadOnly: false,
            [
                new PanelSettingItem("panelName", "面板名称", ValueOrDash(setting?.PanelName), ReadOnly: false, Hint: "支持通过写操作修改（中等风险，不重启面板）。"),
                new PanelSettingItem("systemVersion", "面板版本", ValueOrDash(setting?.SystemVersion)),
                new PanelSettingItem("edition", "版本类型", EditionText(setting?.Edition)),
                new PanelSettingItem("serverPort", "面板端口", ValueOrDash(setting?.ServerPort ?? setting?.Port), ReadOnly: true, Hint: readonlyHint),
                new PanelSettingItem("securityEntrance", "安全入口", MaskEntrance(setting?.SecurityEntrance), ReadOnly: true, Sensitive: true, Hint: readonlyHint),
                new PanelSettingItem("bindDomain", "绑定域名", ValueOrDash(setting?.BindDomain), ReadOnly: true, Hint: readonlyHint),
                new PanelSettingItem("ssl", "面板 SSL", EnableText(setting?.Ssl), ReadOnly: true, Hint: readonlyHint),
                new PanelSettingItem("sslType", "SSL 类型", ValueOrDash(setting?.SslType), ReadOnly: true),
                new PanelSettingItem("sslCertificate", "证书状态", ssl is null ? "未读取" : ssl.HasCertificate ? "已配置" : "未配置"),
                new PanelSettingItem("sslExpireAt", "证书到期时间", ValueOrDash(ssl?.Timeout)),
                new PanelSettingItem("defaultWebsiteDir", "默认网站目录", ValueOrDash(websiteDir?.ResolvedDir))
            ]);
    }

    private static PanelSettingSection BuildSecuritySection(
        PanelSettingInfoDto? setting,
        PanelCurrentUserDto? currentUser) =>
        new(
            "security",
            "安全策略",
            "登录过期、密码复杂度与 MFA 状态；修改密码请使用高危确认的写操作。",
            ReadOnly: false,
            [
                new PanelSettingItem("sessionTimeout", "会话超时", MinutesText(setting?.SessionTimeout)),
                new PanelSettingItem("expirationDays", "密码有效期", DaysText(setting?.ExpirationDays)),
                new PanelSettingItem("complexityVerification", "密码复杂度校验", EnableText(setting?.ComplexityVerification)),
                new PanelSettingItem("mfaStatus", "两步验证（MFA）", EnableText(currentUser?.MfaStatus), ReadOnly: true, Hint: "MFA 开关属高危项，请在 1Panel 面板操作。"),
                new PanelSettingItem("mfaInterval", "MFA 间隔（秒）", ValueOrDash(currentUser?.MfaInterval)),
                new PanelSettingItem("noAuthSetting", "未登录可设置", EnableText(setting?.NoAuthSetting)),
                new PanelSettingItem("allowIps", "面板 IP 白名单", EmptyText(setting?.AllowIps), ReadOnly: true, Hint: "面板 IP 白名单请在 1Panel 面板修改。"),
                new PanelSettingItem("passkeyTrustedProxies", "Passkey 信任代理", EmptyText(setting?.PasskeyTrustedProxies), ReadOnly: true)
            ]);

    private static PanelSettingSection BuildApiSection(PanelCurrentUserDto? currentUser) =>
        new(
            "api",
            "API 接口",
            "1Panel OpenAPI 开关与调用限制。API 开关属于高风险操作，需要显式确认。",
            ReadOnly: false,
            [
                new PanelSettingItem("apiInterfaceStatus", "API 接口", EnableText(currentUser?.ApiInterfaceStatus), ReadOnly: false, Hint: "支持通过写操作切换（高风险）。"),
                new PanelSettingItem("apiKey", "API 密钥", string.IsNullOrWhiteSpace(currentUser?.ApiKey) ? "未设置" : "已设置（不回显）", ReadOnly: true, Sensitive: true),
                new PanelSettingItem("apiKeyValidityTime", "API Key 有效期", EmptyText(currentUser?.ApiKeyValidityTime)),
                new PanelSettingItem("ipWhiteList", "API IP 白名单", EmptyText(currentUser?.IpWhiteList), ReadOnly: true),
                new PanelSettingItem("apiTrustedProxies", "API 信任代理", EmptyText(currentUser?.ApiTrustedProxies), ReadOnly: true)
            ]);

    private static PanelSettingSection BuildTerminalSection(PanelTerminalInfoDto? terminal) =>
        new(
            "terminal",
            "终端设置",
            "1Panel 主机终端开关与显示参数；可参考面板配置调整 PanelFleet 终端默认行为。",
            ReadOnly: false,
            [
                new PanelSettingItem("showTerminalButton", "面板终端按钮", EnableText(terminal?.ShowTerminalButton), ReadOnly: false, Hint: "支持通过写操作切换（中等风险）。"),
                new PanelSettingItem("fontSize", "字号", EmptyText(terminal?.FontSize)),
                new PanelSettingItem("scrollback", "回滚行数", EmptyText(terminal?.Scrollback)),
                new PanelSettingItem("cursorStyle", "光标样式", EmptyText(terminal?.CursorStyle))
            ]);

    private static NodeDescriptor BuildDescriptor(NodeRecord record, INodeSecretProtector protector) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    private NodeDescriptor BuildDescriptor(NodeRecord record) => BuildDescriptor(record, _protector);

    private static string ValueOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未设置" : value;

    private static string EnableText(string? value) => value switch
    {
        null => "—",
        "Enable" or "enable" or "true" => "已开启",
        "Disable" or "disable" or "false" => "已关闭",
        "Mux" or "mux" => "多路复用",
        _ => value
    };

    private static string EditionText(string? value) => value switch
    {
        null or "" => "—",
        "cn" => "中国版（cn）",
        "intl" => "国际版（intl）",
        _ => value
    };

    private static string MinutesText(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            ? $"{minutes} 分钟"
            : ValueOrDash(value);

    private static string DaysText(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            ? days == 0 ? "永不过期" : $"{days} 天"
            : ValueOrDash(value);

    private static string MaskEntrance(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未设置" : $"****（已设置，{value.Length} 位）";
}
