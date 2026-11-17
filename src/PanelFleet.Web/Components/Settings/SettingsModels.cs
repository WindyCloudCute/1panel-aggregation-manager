using System.Globalization;
using PanelFleet.Application.Configuration;
using PanelFleet.Application.Models;
using PanelFleet.Application.Settings;

namespace PanelFleet.Web.Components.Settings;

/// <summary>
/// 设置页视图模型（前端本地定义）。
/// <para>后端 task-27 落地前用于界面与交互开发；DTO 出现后由 <c>SettingsWorkspace</c> 做字段映射，
/// 组件本身只依赖这里的模型，避免编译期耦合后端类型。</para>
/// </summary>
public sealed class PanelFleetSettingsModel
{
    /// <summary>总览刷新间隔（秒）。</summary>
    public int OverviewRefreshSeconds { get; set; } = 30;

    /// <summary>资源刷新间隔（秒）。</summary>
    public int ResourceRefreshSeconds { get; set; } = 60;

    /// <summary>资源缓存 TTL（秒）。</summary>
    public int CacheTtlSeconds { get; set; } = 120;

    /// <summary>列表每页条数。</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>单节点资源采集上限（超过则截断提示）。</summary>
    public int ResourceLimit { get; set; } = 500;

    /// <summary>终端功能总开关。</summary>
    public bool TerminalEnabled { get; set; } = true;

    /// <summary>终端会话空闲超时（分钟）。</summary>
    public int TerminalSessionTimeoutMinutes { get; set; } = 30;

    /// <summary>审计保留天数。</summary>
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>日志页默认拉取条数上限。</summary>
    public int LogLineLimit { get; set; } = 2000;

    // —— 只读信息 ——

    public string? DataDirectory { get; set; }

    public string? ListenUrl { get; set; }

    public string? OidcAuthority { get; set; }

    public string? OidcClientId { get; set; }

    public int NodeCount { get; set; }

    public string? Version { get; set; }

    /// <summary>PanelFleet 当前主题（light / dark，跟随浏览器时为 null）。</summary>
    public string? CurrentTheme { get; set; }

    public PanelFleetSettingsModel Clone() => (PanelFleetSettingsModel)MemberwiseClone();
}

/// <summary>1Panel 面板设置（单节点只读视图）。</summary>
public sealed class PanelSettingsModel
{
    public required string NodeId { get; init; }

    public required string NodeName { get; init; }

    /// <summary>面板访问地址（节点 BaseUrl + 安全入口，用于「打开 1Panel」）。</summary>
    public string? PanelUrl { get; init; }

    public string? PanelName { get; init; }

    public string? SystemVersion { get; init; }

    public string? Edition { get; init; }

    /// <summary>面板端口（1Panel 原始字符串，可能为空）。</summary>
    public string? ServerPort { get; init; }

    /// <summary>安全入口是否已设置；入口本身不明文回显。</summary>
    public bool HasSecurityEntrance { get; init; }

    /// <summary>脱敏后的安全入口提示，例如「已设置（/****）」。</summary>
    public string? SecurityEntranceHint { get; init; }

    public bool SslEnabled { get; init; }

    /// <summary>证书到期时间（本地解析失败时为 null）。</summary>
    public DateTimeOffset? SslExpiresAt { get; init; }

    /// <summary>证书到期原始字符串（解析失败时原样展示）。</summary>
    public string? SslExpiresText { get; init; }

    public bool MfaEnabled { get; init; }

    public bool ApiEnabled { get; init; }

    public bool TerminalEnabled { get; init; }

    public string? DefaultWebsiteDirectory { get; init; }

    public string? BindAddress { get; init; }
}

/// <summary>设置页分区：PanelFleet 运行参数 / 1Panel 面板设置。</summary>
public enum SettingsSection
{
    PanelFleet,
    Panel
}

/// <summary>字段级校验错误（后端 400 时映射为 field + message）。</summary>
public sealed record SettingsFieldError(string Field, string Message);

/// <summary>PanelFleet 设置保存结果：成功或字段级错误。</summary>
public sealed record PanelFleetSaveResult(bool Success, IReadOnlyList<SettingsFieldError> Errors, string? Message)
{
    public static PanelFleetSaveResult Ok() => new(true, [], null);

    public static PanelFleetSaveResult Invalid(IReadOnlyList<SettingsFieldError> errors, string? message = null) =>
        new(false, errors, message);
}

/// <summary>1Panel 面板设置写操作类型（映射到写操作内核的 Kind）。</summary>
public enum PanelSettingsAction
{
    Rename,
    ChangePassword,
    ToggleApi,
    ToggleTerminal
}

/// <summary>
/// 写操作内核 Kind 常量：与 <c>PanelFleet.Application.Settings.PanelSettingOperationHandlers</c> 中的 HandlerKind 保持一致。
/// </summary>
public static class PanelSettingsOperationKinds
{
    /// <summary>面板名称 / 面板终端开关（Medium，参数 panelName 或 terminalEnabled）。</summary>
    public const string Rename = "panel.setting.update";

    /// <summary>面板登录密码（High，参数 oldPassword / newPassword，需输入确认文本）。</summary>
    public const string ChangePassword = "panel.password.update";

    /// <summary>API 接口开关（High，参数 apiInterfaceStatus=Enable|Disable）。</summary>
    public const string ToggleApi = "panel.api.update";

    /// <summary>面板终端开关，与改名共用 panel.setting.update（参数 terminalEnabled）。</summary>
    public const string ToggleTerminal = "panel.setting.update";
}

/// <summary>后端设置 DTO 与设置页视图模型之间的映射（唯一耦合点，后端字段变化只改这里）。</summary>
public static class SettingsMapping
{
    public static void ApplyFleetValues(PanelFleetSettingsModel target, PanelFleetSettingsValues values)
    {
        target.OverviewRefreshSeconds = values.OverviewRefreshIntervalSeconds;
        target.ResourceRefreshSeconds = values.ResourcesRefreshIntervalSeconds;
        target.CacheTtlSeconds = values.ResourcesCacheTtlSeconds;
        target.PageSize = values.ResourcesPageSize;
        target.ResourceLimit = values.ResourcesPerNodeLimit;
        target.TerminalEnabled = values.TerminalEnabled;
        target.TerminalSessionTimeoutMinutes = Math.Max(1, values.TerminalIdleTimeoutSeconds / 60);
        target.AuditRetentionDays = values.AuditRetentionDays;
        target.LogLineLimit = values.LogEntryLimit;
    }

    public static PanelFleetSettingsUpdate ToFleetUpdate(PanelFleetSettingsModel model) => new()
    {
        OverviewRefreshIntervalSeconds = model.OverviewRefreshSeconds,
        ResourcesRefreshIntervalSeconds = model.ResourceRefreshSeconds,
        ResourcesCacheTtlSeconds = model.CacheTtlSeconds,
        ResourcesPageSize = model.PageSize,
        ResourcesPerNodeLimit = model.ResourceLimit,
        TerminalEnabled = model.TerminalEnabled,
        TerminalIdleTimeoutSeconds = Math.Max(1, model.TerminalSessionTimeoutMinutes) * 60,
        AuditRetentionDays = model.AuditRetentionDays,
        LogEntryLimit = model.LogLineLimit,
    };

    /// <summary>后端 400 响应键（camelCase 配置键）→ 表单字段名。</summary>
    public static IReadOnlyList<SettingsFieldError> MapFleetErrors(IReadOnlyDictionary<string, string[]> errors) =>
        errors
            .SelectMany(pair => pair.Value.Select(message => new SettingsFieldError(FleetFieldFor(pair.Key), message)))
            .ToArray();

    private static string FleetFieldFor(string key) => key switch
    {
        "overviewRefreshIntervalSeconds" => "overviewRefreshSeconds",
        "resourcesRefreshIntervalSeconds" => "resourceRefreshSeconds",
        "resourcesCacheTtlSeconds" => "cacheTtlSeconds",
        "resourcesPageSize" => "pageSize",
        "resourcesPerNodeLimit" => "resourceLimit",
        "terminalIdleTimeoutSeconds" => "terminalSessionTimeoutMinutes",
        "auditRetentionDays" => "auditRetentionDays",
        "logEntryLimit" => "logLineLimit",
        _ => string.Empty,
    };

    public static PanelSettingsModel ToPanelModel(PanelSettingsView view, NodeSummary node)
    {
        var items = view.Sections
            .SelectMany(section => section.Items)
            .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

        string? Value(string key) => items.TryGetValue(key, out var item) ? item.Value : null;

        var entrance = string.IsNullOrWhiteSpace(node.SecurityEntrance)
            ? string.Empty
            : "/" + node.SecurityEntrance.Trim('/');
        var expireText = Value("sslExpireAt");
        var expiresAt = DateTimeOffset.TryParse(
            expireText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : (DateTimeOffset?)null;

        return new PanelSettingsModel
        {
            NodeId = view.NodeId.ToString("D"),
            NodeName = string.IsNullOrWhiteSpace(view.NodeName) ? node.Name : view.NodeName,
            PanelUrl = node.BaseUrl.AbsoluteUri.TrimEnd('/') + entrance,
            PanelName = Value("panelName"),
            SystemVersion = Value("systemVersion"),
            Edition = Value("edition"),
            ServerPort = Value("serverPort"),
            HasSecurityEntrance = !string.IsNullOrWhiteSpace(node.SecurityEntrance),
            SecurityEntranceHint = Value("securityEntrance"),
            SslEnabled = IsEnabled(Value("ssl")),
            SslExpiresAt = expiresAt,
            SslExpiresText = expireText,
            MfaEnabled = IsEnabled(Value("mfaStatus")),
            ApiEnabled = IsEnabled(Value("apiInterfaceStatus")),
            TerminalEnabled = IsEnabled(Value("showTerminalButton")),
            DefaultWebsiteDirectory = Value("defaultWebsiteDir"),
            BindAddress = Value("bindDomain"),
        };
    }

    private static bool IsEnabled(string? value) =>
        value is "已开启" or "已启用" or "Enable" or "enable" or "true" or "True";
}
