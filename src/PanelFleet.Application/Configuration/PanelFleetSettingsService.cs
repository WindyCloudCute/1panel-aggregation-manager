using System.Text.Json;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;

namespace PanelFleet.Application.Configuration;

/// <summary>一次 PanelFleet 设置更新结果（含生效后的视图与实际变更项）。</summary>
public sealed record PanelFleetSettingsResult(PanelFleetSettingsView View, IReadOnlyList<string> ChangedKeys);

/// <summary>
/// PanelFleet 设置服务（scoped）：对外提供设置视图，并负责「校验 → 持久化 → 热应用 → 审计」的更新流程。
/// </summary>
public sealed class PanelFleetSettingsService
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private readonly IPanelFleetSettingsRuntime _runtime;
    private readonly IAuditStore _audit;
    private readonly ILogger<PanelFleetSettingsService> _logger;

    public PanelFleetSettingsService(
        IPanelFleetSettingsRuntime runtime,
        IAuditStore audit,
        ILogger<PanelFleetSettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _runtime = runtime;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>读取当前生效设置、默认值与来源。</summary>
    public PanelFleetSettingsView GetView() => new(
        _runtime.Revision,
        _runtime.UpdatedAt,
        _runtime.Current,
        _runtime.Defaults,
        _runtime.Sources,
        PanelFleetSettingsCatalog.Fields);

    /// <summary>应用一份局部更新；校验失败抛 <see cref="PanelFleetSettingsValidationException"/>。</summary>
    public async Task<PanelFleetSettingsResult> UpdateAsync(
        PanelFleetSettingsUpdate update,
        ActorInfo actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(actor);

        var previous = _runtime.Current;
        var values = PanelFleetSettingsCatalog.Apply(previous, update);
        var change = _runtime.Update(values);

        await WriteAuditAsync(actor, previous, change, cancellationToken);

        return new PanelFleetSettingsResult(GetView(), change.ChangedKeys);
    }

    private async Task WriteAuditAsync(
        ActorInfo actor,
        PanelFleetSettingsValues previous,
        PanelFleetSettingsChange change,
        CancellationToken cancellationToken)
    {
        try
        {
            var before = new Dictionary<string, string>(StringComparer.Ordinal);
            var after = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in change.ChangedKeys)
            {
                before[key] = PanelFleetSettingsCatalog.FormatValue(previous, key);
                after[key] = PanelFleetSettingsCatalog.FormatValue(change.Current, key);
            }

            var detail = new
            {
                revision = change.Revision,
                changedKeys = change.ChangedKeys,
                before,
                after,
                updatedAt = change.UpdatedAt
            };

            var entry = new AuditEntry(
                Guid.NewGuid(),
                change.UpdatedAt,
                actor,
                "settings.updated",
                "settings",
                "panelfleet",
                null,
                JsonSerializer.Serialize(detail, AuditJson));

            await _audit.AddAsync(entry, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "写入审计失败：{Action}", "settings.updated");
        }
    }
}
