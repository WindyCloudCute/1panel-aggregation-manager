using System.Globalization;
using PanelFleet.Application.Overview;
using PanelFleet.Application.Resources;
using PanelFleet.Application.Terminal;

namespace PanelFleet.Application.Configuration;

/// <summary>可热更新的 PanelFleet 设置取值（一项一字段，整份不可为空）。</summary>
public sealed record PanelFleetSettingsValues
{
    /// <summary>总览自动刷新间隔（秒）；0 表示关闭自动刷新。</summary>
    public int OverviewRefreshIntervalSeconds { get; init; } = 30;

    /// <summary>总览缓存有效期（秒）。</summary>
    public int OverviewCacheTtlSeconds { get; init; } = 25;

    /// <summary>资源清单自动刷新间隔（秒）；0 表示关闭自动刷新。</summary>
    public int ResourcesRefreshIntervalSeconds { get; init; } = 60;

    /// <summary>资源清单缓存有效期（秒）。</summary>
    public int ResourcesCacheTtlSeconds { get; init; } = 30;

    /// <summary>资源清单默认每页条数。</summary>
    public int ResourcesPageSize { get; init; } = 50;

    /// <summary>单个节点最多采集的资源条数。</summary>
    public int ResourcesPerNodeLimit { get; init; } = 200;

    /// <summary>终端会话空闲超时（秒）。</summary>
    public int TerminalIdleTimeoutSeconds { get; init; } = 1800;

    /// <summary>是否启用 Web 终端。</summary>
    public bool TerminalEnabled { get; init; } = true;

    /// <summary>审计记录保留天数。</summary>
    public int AuditRetentionDays { get; init; } = 90;

    /// <summary>日志查看默认返回条数上限。</summary>
    public int LogEntryLimit { get; init; } = 200;
}

/// <summary>PanelFleet 设置的局部更新请求（未提供的项保持不变）。</summary>
public sealed class PanelFleetSettingsUpdate
{
    public int? OverviewRefreshIntervalSeconds { get; set; }

    public int? OverviewCacheTtlSeconds { get; set; }

    public int? ResourcesRefreshIntervalSeconds { get; set; }

    public int? ResourcesCacheTtlSeconds { get; set; }

    public int? ResourcesPageSize { get; set; }

    public int? ResourcesPerNodeLimit { get; set; }

    public int? TerminalIdleTimeoutSeconds { get; set; }

    public bool? TerminalEnabled { get; set; }

    public int? AuditRetentionDays { get; set; }

    public int? LogEntryLimit { get; set; }

    /// <summary>本次请求实际提供的设置项 key（camelCase）。</summary>
    public IReadOnlyList<string> ProvidedKeys()
    {
        var keys = new List<string>();
        if (OverviewRefreshIntervalSeconds is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.OverviewRefreshIntervalKey);
        }

        if (OverviewCacheTtlSeconds is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.OverviewCacheTtlKey);
        }

        if (ResourcesRefreshIntervalSeconds is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.ResourcesRefreshIntervalKey);
        }

        if (ResourcesCacheTtlSeconds is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.ResourcesCacheTtlKey);
        }

        if (ResourcesPageSize is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.ResourcesPageSizeKey);
        }

        if (ResourcesPerNodeLimit is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.ResourcesPerNodeLimitKey);
        }

        if (TerminalIdleTimeoutSeconds is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.TerminalIdleTimeoutKey);
        }

        if (TerminalEnabled is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.TerminalEnabledKey);
        }

        if (AuditRetentionDays is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.AuditRetentionDaysKey);
        }

        if (LogEntryLimit is not null)
        {
            keys.Add(PanelFleetSettingsCatalog.LogEntryLimitKey);
        }

        return keys;
    }
}

/// <summary>设置项的取值类型。</summary>
public enum SettingsFieldKind
{
    /// <summary>整数。</summary>
    Integer,

    /// <summary>布尔开关。</summary>
    Boolean
}

/// <summary>一个可编辑设置项的描述（用于设置页渲染表单与错误提示）。</summary>
public sealed record PanelFleetSettingField(
    string Key,
    string Label,
    string Description,
    SettingsFieldKind Kind,
    int? Min = null,
    int? Max = null,
    string? Unit = null,
    bool AllowZero = false);

/// <summary>设置校验失败（键 → 中文错误信息）。</summary>
public sealed class PanelFleetSettingsValidationException : Exception
{
    public PanelFleetSettingsValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("设置校验失败。")
    {
        Errors = errors;
    }

    /// <summary>逐字段错误信息。</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>PanelFleet 设置目录：字段清单、默认值捕获、取值读写、校验与合并。</summary>
public static class PanelFleetSettingsCatalog
{
    public const string OverviewRefreshIntervalKey = "overviewRefreshIntervalSeconds";
    public const string OverviewCacheTtlKey = "overviewCacheTtlSeconds";
    public const string ResourcesRefreshIntervalKey = "resourcesRefreshIntervalSeconds";
    public const string ResourcesCacheTtlKey = "resourcesCacheTtlSeconds";
    public const string ResourcesPageSizeKey = "resourcesPageSize";
    public const string ResourcesPerNodeLimitKey = "resourcesPerNodeLimit";
    public const string TerminalIdleTimeoutKey = "terminalIdleTimeoutSeconds";
    public const string TerminalEnabledKey = "terminalEnabled";
    public const string AuditRetentionDaysKey = "auditRetentionDays";
    public const string LogEntryLimitKey = "logEntryLimit";

    private static readonly PanelFleetSettingField[] FieldList =
    [
        new(OverviewRefreshIntervalKey, "总览刷新间隔", "首页总览自动刷新间隔；0 表示关闭自动刷新。", SettingsFieldKind.Integer, 10, 3600, "秒", AllowZero: true),
        new(OverviewCacheTtlKey, "总览缓存有效期", "总览缓存在内存中保留的时间。", SettingsFieldKind.Integer, 5, 600, "秒"),
        new(ResourcesRefreshIntervalKey, "资源刷新间隔", "资源清单后台刷新间隔；0 表示关闭后台刷新。", SettingsFieldKind.Integer, 10, 3600, "秒", AllowZero: true),
        new(ResourcesCacheTtlKey, "资源缓存有效期", "资源清单缓存在内存中保留的时间。", SettingsFieldKind.Integer, 5, 600, "秒"),
        new(ResourcesPageSizeKey, "资源每页条数", "资源清单表格默认每页展示条数。", SettingsFieldKind.Integer, 10, 500, "条"),
        new(ResourcesPerNodeLimitKey, "每节点资源上限", "单个节点最多采集的资源条数，超出会截断并提示。", SettingsFieldKind.Integer, 20, 1000, "条"),
        new(TerminalIdleTimeoutKey, "终端空闲超时", "Web 终端无操作多久后自动关闭。", SettingsFieldKind.Integer, 30, 86400, "秒"),
        new(TerminalEnabledKey, "启用终端", "关闭后所有节点终端入口不可用。", SettingsFieldKind.Boolean),
        new(AuditRetentionDaysKey, "审计保留天数", "超过保留期的审计记录可被清理。", SettingsFieldKind.Integer, 1, 3650, "天"),
        new(LogEntryLimitKey, "日志条数上限", "日志查看默认返回的最大条数。", SettingsFieldKind.Integer, 50, 10000, "条")
    ];

    /// <summary>全部设置项（顺序固定，即设置页展示顺序）。</summary>
    public static IReadOnlyList<PanelFleetSettingField> Fields => FieldList;

    /// <summary>按 key（大小写不敏感）查找设置项；未知 key 返回 <c>null</c>。</summary>
    public static PanelFleetSettingField? Find(string key) =>
        FieldList.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>从现有配置对象捕获默认值（同时作为未持久化时的初值）。</summary>
    public static PanelFleetSettingsValues CaptureDefaults(
        OverviewOptions overview,
        ResourceOptions resources,
        TerminalOptions? terminal) =>
        new()
        {
            OverviewRefreshIntervalSeconds = overview.RefreshIntervalSeconds,
            OverviewCacheTtlSeconds = overview.CacheTtlSeconds,
            ResourcesRefreshIntervalSeconds = resources.RefreshIntervalSeconds,
            ResourcesCacheTtlSeconds = resources.CacheTtlSeconds,
            ResourcesPageSize = 50,
            ResourcesPerNodeLimit = resources.PerNodeLimit,
            TerminalIdleTimeoutSeconds = terminal?.IdleTimeoutSeconds ?? 1800,
            TerminalEnabled = terminal?.Enabled ?? true,
            AuditRetentionDays = 90,
            LogEntryLimit = 200
        };

    /// <summary>读取整数项（key 未知时抛 <see cref="ArgumentOutOfRangeException"/>）。</summary>
    public static int ReadInteger(PanelFleetSettingsValues values, string key) => key switch
    {
        OverviewRefreshIntervalKey => values.OverviewRefreshIntervalSeconds,
        OverviewCacheTtlKey => values.OverviewCacheTtlSeconds,
        ResourcesRefreshIntervalKey => values.ResourcesRefreshIntervalSeconds,
        ResourcesCacheTtlKey => values.ResourcesCacheTtlSeconds,
        ResourcesPageSizeKey => values.ResourcesPageSize,
        ResourcesPerNodeLimitKey => values.ResourcesPerNodeLimit,
        TerminalIdleTimeoutKey => values.TerminalIdleTimeoutSeconds,
        AuditRetentionDaysKey => values.AuditRetentionDays,
        LogEntryLimitKey => values.LogEntryLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "不是整数类型的设置项。")
    };

    /// <summary>读取布尔项（key 未知时抛 <see cref="ArgumentOutOfRangeException"/>）。</summary>
    public static bool ReadBoolean(PanelFleetSettingsValues values, string key) => key switch
    {
        TerminalEnabledKey => values.TerminalEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "不是布尔类型的设置项。")
    };

    /// <summary>写入整数项，返回新值。</summary>
    public static PanelFleetSettingsValues WriteInteger(PanelFleetSettingsValues values, string key, int value) => key switch
    {
        OverviewRefreshIntervalKey => values with { OverviewRefreshIntervalSeconds = value },
        OverviewCacheTtlKey => values with { OverviewCacheTtlSeconds = value },
        ResourcesRefreshIntervalKey => values with { ResourcesRefreshIntervalSeconds = value },
        ResourcesCacheTtlKey => values with { ResourcesCacheTtlSeconds = value },
        ResourcesPageSizeKey => values with { ResourcesPageSize = value },
        ResourcesPerNodeLimitKey => values with { ResourcesPerNodeLimit = value },
        TerminalIdleTimeoutKey => values with { TerminalIdleTimeoutSeconds = value },
        AuditRetentionDaysKey => values with { AuditRetentionDays = value },
        LogEntryLimitKey => values with { LogEntryLimit = value },
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "不是整数类型的设置项。")
    };

    /// <summary>写入布尔项，返回新值。</summary>
    public static PanelFleetSettingsValues WriteBoolean(PanelFleetSettingsValues values, string key, bool value) => key switch
    {
        TerminalEnabledKey => values with { TerminalEnabled = value },
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "不是布尔类型的设置项。")
    };

    /// <summary>校验单个整数项；合法返回 <c>null</c>，否则返回中文错误信息。</summary>
    public static string? ValidateInteger(PanelFleetSettingField field, int value)
    {
        if (value == 0 && field.AllowZero)
        {
            return null;
        }

        var min = field.Min ?? int.MinValue;
        var max = field.Max ?? int.MaxValue;
        if (value < min || value > max)
        {
            var range = field.AllowZero
                ? $"0（关闭）或 {min}..{max}"
                : $"{min}..{max}";
            return $"{field.Label}必须在 {range} 之间（当前 {value}）。";
        }

        return null;
    }

    /// <summary>校验整份取值，返回所有错误项。</summary>
    public static IReadOnlyDictionary<string, string[]> ValidateAll(PanelFleetSettingsValues values)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in FieldList)
        {
            if (field.Kind != SettingsFieldKind.Integer)
            {
                continue;
            }

            var error = ValidateInteger(field, ReadInteger(values, field.Key));
            if (error is not null)
            {
                errors[field.Key] = [error];
            }
        }

        return errors;
    }

    /// <summary>把局部更新合并到当前取值；空提交或校验失败时抛 <see cref="PanelFleetSettingsValidationException"/>。</summary>
    public static PanelFleetSettingsValues Apply(PanelFleetSettingsValues current, PanelFleetSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(update);

        var provided = update.ProvidedKeys();
        if (provided.Count == 0)
        {
            throw new PanelFleetSettingsValidationException(new Dictionary<string, string[]>
            {
                ["update"] = ["没有需要更新的设置项。"]
            });
        }

        var values = current;
        if (update.OverviewRefreshIntervalSeconds is { } overviewRefreshIntervalSeconds)
        {
            values = values with { OverviewRefreshIntervalSeconds = overviewRefreshIntervalSeconds };
        }

        if (update.OverviewCacheTtlSeconds is { } overviewCacheTtlSeconds)
        {
            values = values with { OverviewCacheTtlSeconds = overviewCacheTtlSeconds };
        }

        if (update.ResourcesRefreshIntervalSeconds is { } resourcesRefreshIntervalSeconds)
        {
            values = values with { ResourcesRefreshIntervalSeconds = resourcesRefreshIntervalSeconds };
        }

        if (update.ResourcesCacheTtlSeconds is { } resourcesCacheTtlSeconds)
        {
            values = values with { ResourcesCacheTtlSeconds = resourcesCacheTtlSeconds };
        }

        if (update.ResourcesPageSize is { } resourcesPageSize)
        {
            values = values with { ResourcesPageSize = resourcesPageSize };
        }

        if (update.ResourcesPerNodeLimit is { } resourcesPerNodeLimit)
        {
            values = values with { ResourcesPerNodeLimit = resourcesPerNodeLimit };
        }

        if (update.TerminalIdleTimeoutSeconds is { } terminalIdleTimeoutSeconds)
        {
            values = values with { TerminalIdleTimeoutSeconds = terminalIdleTimeoutSeconds };
        }

        if (update.TerminalEnabled is { } terminalEnabled)
        {
            values = values with { TerminalEnabled = terminalEnabled };
        }

        if (update.AuditRetentionDays is { } auditRetentionDays)
        {
            values = values with { AuditRetentionDays = auditRetentionDays };
        }

        if (update.LogEntryLimit is { } logEntryLimit)
        {
            values = values with { LogEntryLimit = logEntryLimit };
        }

        var errors = ValidateAll(values);
        if (errors.Count > 0)
        {
            throw new PanelFleetSettingsValidationException(errors);
        }

        return values;
    }

    /// <summary>把设置项格式化为用于审计与比较的稳定字符串。</summary>
    public static string FormatValue(PanelFleetSettingsValues values, string key)
    {
        var field = Find(key);
        if (field is null)
        {
            return string.Empty;
        }

        return field.Kind == SettingsFieldKind.Boolean
            ? (ReadBoolean(values, key) ? "true" : "false")
            : ReadInteger(values, key).ToString(CultureInfo.InvariantCulture);
    }
}
