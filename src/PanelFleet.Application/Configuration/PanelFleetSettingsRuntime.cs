using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Overview;
using PanelFleet.Application.Resources;
using PanelFleet.Application.Terminal;

namespace PanelFleet.Application.Configuration;

/// <summary>一次设置变更（已生效）。</summary>
public sealed record PanelFleetSettingsChange(
    PanelFleetSettingsValues Previous,
    PanelFleetSettingsValues Current,
    IReadOnlyList<string> ChangedKeys,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>PanelFleet 运行时设置视图（GET /api/settings/panelfleet 的响应主体）。</summary>
public sealed record PanelFleetSettingsView(
    long Revision,
    DateTimeOffset UpdatedAt,
    PanelFleetSettingsValues Values,
    PanelFleetSettingsValues Defaults,
    IReadOnlyDictionary<string, string> Sources,
    IReadOnlyList<PanelFleetSettingField> Fields);

/// <summary>
/// PanelFleet 设置运行时：持有生效值、把持久化覆盖应用到配置对象（热更新）、并在变更时广播。
/// <para>单例；首次解析时同步读取设置文件并应用，保证进程重启后持久化值立即生效。</para>
/// <para>可直接热应用的项：<see cref="OverviewOptions"/>、<see cref="ResourceOptions"/>、
/// <see cref="TerminalOptions"/>（终端按会话读取，立即生效）。后台刷新宿主的 <c>PeriodicTimer</c>
/// 在启动时创建，需宿主订阅 <see cref="Changed"/> 后重建定时器才会生效，该接线由 Host 负责。</para>
/// </summary>
public interface IPanelFleetSettingsRuntime
{
    /// <summary>当前生效值。</summary>
    PanelFleetSettingsValues Current { get; }

    /// <summary>启动时从配置节捕获的默认值。</summary>
    PanelFleetSettingsValues Defaults { get; }

    /// <summary>持久化修订号（未持久化任何设置时为 0）。</summary>
    long Revision { get; }

    /// <summary>持久化时间（未持久化时为 <see cref="DateTimeOffset.MinValue"/>）。</summary>
    DateTimeOffset UpdatedAt { get; }

    /// <summary>每个设置项当前值的来源：<c>default</c>（配置节/内置默认）或 <c>stored</c>（持久化覆盖）。</summary>
    IReadOnlyDictionary<string, string> Sources { get; }

    /// <summary>设置变更后触发（在持久化与生效之后）。</summary>
    event Action<PanelFleetSettingsChange>? Changed;

    /// <summary>校验并持久化一份完整取值，随后热应用到配置对象并广播。</summary>
    PanelFleetSettingsChange Update(PanelFleetSettingsValues values);
}

/// <inheritdoc />
public sealed class PanelFleetSettingsRuntime : IPanelFleetSettingsRuntime
{
    private readonly object _sync = new();
    private readonly IPanelFleetSettingsStore _store;
    private readonly OverviewOptions _overview;
    private readonly ResourceOptions _resources;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<PanelFleetSettingsRuntime> _logger;
    private readonly HashSet<string> _storedKeys = new(StringComparer.OrdinalIgnoreCase);

    private PanelFleetSettingsValues _current;
    private PanelFleetSettingsValues _defaults;
    private long _revision;
    private DateTimeOffset _updatedAt = DateTimeOffset.MinValue;

    public PanelFleetSettingsRuntime(
        IPanelFleetSettingsStore store,
        OverviewOptions overview,
        ResourceOptions resources,
        IServiceProvider services,
        TimeProvider time,
        ILogger<PanelFleetSettingsRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _overview = overview;
        _resources = resources;
        _services = services;
        _time = time;
        _logger = logger;

        _defaults = PanelFleetSettingsCatalog.CaptureDefaults(overview, resources, ResolveTerminalOptions());
        _current = LoadPersistedOrDefaults(_defaults);
        Apply(_current);
    }

    public PanelFleetSettingsValues Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public PanelFleetSettingsValues Defaults
    {
        get
        {
            lock (_sync)
            {
                return _defaults;
            }
        }
    }

    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    public DateTimeOffset UpdatedAt
    {
        get
        {
            lock (_sync)
            {
                return _updatedAt;
            }
        }
    }

    public IReadOnlyDictionary<string, string> Sources
    {
        get
        {
            lock (_sync)
            {
                var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var descriptor in PanelFleetSettingsCatalog.Fields)
                {
                    sources[descriptor.Key] = _storedKeys.Contains(descriptor.Key) ? "stored" : "default";
                }

                return sources;
            }
        }
    }

    public event Action<PanelFleetSettingsChange>? Changed;

    public PanelFleetSettingsChange Update(PanelFleetSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var errors = PanelFleetSettingsCatalog.ValidateAll(values);
        if (errors.Count > 0)
        {
            throw new PanelFleetSettingsValidationException(errors);
        }

        PanelFleetSettingsChange change;
        lock (_sync)
        {
            var previous = _current;
            var changedKeys = PanelFleetSettingsCatalog.Fields
                .Where(descriptor => !string.Equals(
                    PanelFleetSettingsCatalog.FormatValue(previous, descriptor.Key),
                    PanelFleetSettingsCatalog.FormatValue(values, descriptor.Key),
                    StringComparison.Ordinal))
                .Select(descriptor => descriptor.Key)
                .ToArray();

            var revision = _revision + 1;
            var updatedAt = _time.GetUtcNow();

            var storedValues = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in PanelFleetSettingsCatalog.Fields)
            {
                if (descriptor.Kind == SettingsFieldKind.Boolean)
                {
                    storedValues[descriptor.Key] = JsonSerializer.SerializeToElement(
                        PanelFleetSettingsCatalog.ReadBoolean(values, descriptor.Key));
                }
                else
                {
                    storedValues[descriptor.Key] = JsonSerializer.SerializeToElement(
                        PanelFleetSettingsCatalog.ReadInteger(values, descriptor.Key));
                }
            }

            _store.Save(new PanelFleetSettingsDocument(revision, updatedAt, storedValues));

            _current = values;
            _revision = revision;
            _updatedAt = updatedAt;
            foreach (var descriptor in PanelFleetSettingsCatalog.Fields)
            {
                _storedKeys.Add(descriptor.Key);
            }

            Apply(values);
            change = new PanelFleetSettingsChange(previous, values, changedKeys, revision, updatedAt);
        }

        _logger.LogInformation(
            "PanelFleet 设置已更新 revision {Revision}，变更 {Count} 项：{Keys}",
            change.Revision,
            change.ChangedKeys.Count,
            string.Join(",", change.ChangedKeys));

        Changed?.Invoke(change);
        return change;
    }

    private PanelFleetSettingsValues LoadPersistedOrDefaults(PanelFleetSettingsValues defaults)
    {
        var document = _store.Load();
        if (document is null)
        {
            return defaults;
        }

        var values = defaults;
        foreach (var pair in document.Values)
        {
            var descriptor = PanelFleetSettingsCatalog.Find(pair.Key);
            if (descriptor is null)
            {
                _logger.LogWarning("忽略未知的持久化设置项 {Key}", pair.Key);
                continue;
            }

            if (!TryConvert(pair.Value, descriptor, out var converted, out var error))
            {
                _logger.LogWarning("忽略非法的持久化设置项 {Key}：{Error}", pair.Key, error);
                continue;
            }

            if (descriptor.Kind == SettingsFieldKind.Integer
                && PanelFleetSettingsCatalog.ValidateInteger(descriptor, converted.Integer) is { } validationError)
            {
                _logger.LogWarning("忽略越界的持久化设置项 {Key}：{Error}", pair.Key, validationError);
                continue;
            }

            values = descriptor.Kind == SettingsFieldKind.Boolean
                ? PanelFleetSettingsCatalog.WriteBoolean(values, descriptor.Key, converted.Boolean)
                : PanelFleetSettingsCatalog.WriteInteger(values, descriptor.Key, converted.Integer);
            _storedKeys.Add(descriptor.Key);
        }

        _revision = document.Revision;
        _updatedAt = document.UpdatedAt;
        return values;
    }

    private static bool TryConvert(
        JsonElement element,
        PanelFleetSettingField descriptor,
        out ConvertedValue converted,
        out string? error)
    {
        converted = default;
        error = null;

        switch (descriptor.Kind)
        {
            case SettingsFieldKind.Integer:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
                {
                    converted = new ConvertedValue(number, false);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String
                    && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    converted = new ConvertedValue(number, false);
                    return true;
                }

                error = "需要整数。";
                return false;

            case SettingsFieldKind.Boolean:
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    converted = new ConvertedValue(0, element.GetBoolean());
                    return true;
                }

                error = "需要布尔值。";
                return false;

            default:
                error = "未知类型。";
                return false;
        }
    }

    private void Apply(PanelFleetSettingsValues values)
    {
        _overview.RefreshIntervalSeconds = values.OverviewRefreshIntervalSeconds;
        _overview.CacheTtlSeconds = values.OverviewCacheTtlSeconds;

        _resources.RefreshIntervalSeconds = values.ResourcesRefreshIntervalSeconds;
        _resources.CacheTtlSeconds = values.ResourcesCacheTtlSeconds;
        _resources.PerNodeLimit = values.ResourcesPerNodeLimit;

        var terminal = ResolveTerminalOptions();
        if (terminal is not null)
        {
            terminal.Enabled = values.TerminalEnabled;
            terminal.IdleTimeoutSeconds = values.TerminalIdleTimeoutSeconds;
        }
    }

    private TerminalOptions? ResolveTerminalOptions() => _services.GetService<TerminalOptions>();

    /// <summary>持久化值的转换结果（整数项用 <see cref="Integer"/>，布尔项用 <see cref="Boolean"/>）。</summary>
    private readonly record struct ConvertedValue(int Integer, bool Boolean);
}
