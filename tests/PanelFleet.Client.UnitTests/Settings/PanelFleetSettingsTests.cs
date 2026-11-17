using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Configuration;
using PanelFleet.Application.Overview;
using PanelFleet.Application.Resources;
using PanelFleet.Application.Terminal;

namespace PanelFleet.Client.UnitTests.Settings;

public sealed class PanelFleetSettingsTests
{
    [Fact]
    public void Catalog_HasUniqueKeys_AndTenFields()
    {
        var keys = PanelFleetSettingsCatalog.Fields.Select(field => field.Key).ToArray();

        Assert.Equal(10, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(key)));
    }

    [Fact]
    public void Defaults_AreValid()
    {
        var defaults = new PanelFleetSettingsValues();

        var errors = PanelFleetSettingsCatalog.ValidateAll(defaults);

        Assert.Empty(errors);
        Assert.Equal("30", PanelFleetSettingsCatalog.FormatValue(defaults, PanelFleetSettingsCatalog.OverviewRefreshIntervalKey));
        Assert.Equal("true", PanelFleetSettingsCatalog.FormatValue(defaults, PanelFleetSettingsCatalog.TerminalEnabledKey));
    }

    [Fact]
    public void Apply_EmptyUpdate_Throws()
    {
        var current = new PanelFleetSettingsValues();

        var exception = Assert.Throws<PanelFleetSettingsValidationException>(
            () => PanelFleetSettingsCatalog.Apply(current, new PanelFleetSettingsUpdate()));

        Assert.Contains("update", exception.Errors.Keys);
    }

    [Fact]
    public void Apply_OutOfRange_ReportsFieldKey()
    {
        var current = new PanelFleetSettingsValues();
        var update = new PanelFleetSettingsUpdate { ResourcesPageSize = 1 };

        var exception = Assert.Throws<PanelFleetSettingsValidationException>(
            () => PanelFleetSettingsCatalog.Apply(current, update));

        Assert.Contains(PanelFleetSettingsCatalog.ResourcesPageSizeKey, exception.Errors.Keys);
    }

    [Fact]
    public void Apply_AllowsZero_ForRefreshInterval()
    {
        var current = new PanelFleetSettingsValues();
        var update = new PanelFleetSettingsUpdate { OverviewRefreshIntervalSeconds = 0, TerminalEnabled = false };

        var values = PanelFleetSettingsCatalog.Apply(current, update);

        Assert.Equal(0, values.OverviewRefreshIntervalSeconds);
        Assert.False(values.TerminalEnabled);
        Assert.Equal(60, values.ResourcesRefreshIntervalSeconds);
    }

    [Fact]
    public void Apply_RejectsDisabledZero_ForCacheTtl()
    {
        var current = new PanelFleetSettingsValues();
        var update = new PanelFleetSettingsUpdate { OverviewCacheTtlSeconds = 0 };

        var exception = Assert.Throws<PanelFleetSettingsValidationException>(
            () => PanelFleetSettingsCatalog.Apply(current, update));

        Assert.Contains(PanelFleetSettingsCatalog.OverviewCacheTtlKey, exception.Errors.Keys);
    }

    [Fact]
    public void ProvidedKeys_ReflectOnlyAssignedValues()
    {
        var update = new PanelFleetSettingsUpdate
        {
            AuditRetentionDays = 30,
            TerminalEnabled = true
        };

        var keys = update.ProvidedKeys();

        Assert.Equal(2, keys.Count);
        Assert.Contains(PanelFleetSettingsCatalog.AuditRetentionDaysKey, keys);
        Assert.Contains(PanelFleetSettingsCatalog.TerminalEnabledKey, keys);
    }

    [Fact]
    public void Store_RoundTripsDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panelfleet-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFilePanelFleetSettingsStore(path, NullLogger<JsonFilePanelFleetSettingsStore>.Instance);
            var document = new PanelFleetSettingsDocument(
                7,
                new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero),
                new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    [PanelFleetSettingsCatalog.ResourcesPageSizeKey] = JsonSerializer.SerializeToElement(120),
                    [PanelFleetSettingsCatalog.TerminalEnabledKey] = JsonSerializer.SerializeToElement(false)
                });

            store.Save(document);
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(7, loaded.Revision);
            Assert.Equal(120, loaded.Values[PanelFleetSettingsCatalog.ResourcesPageSizeKey].GetInt32());
            Assert.False(loaded.Values[PanelFleetSettingsCatalog.TerminalEnabledKey].GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_IgnoresCorruptedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panelfleet-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var store = new JsonFilePanelFleetSettingsStore(path, NullLogger<JsonFilePanelFleetSettingsStore>.Instance);

            Assert.Null(store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Runtime_AppliesPersistedValues_AndRaisesChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panelfleet-settings-{Guid.NewGuid():N}.json");
        try
        {
            var seed = new JsonFilePanelFleetSettingsStore(path, NullLogger<JsonFilePanelFleetSettingsStore>.Instance);
            seed.Save(new PanelFleetSettingsDocument(
                3,
                DateTimeOffset.UtcNow,
                new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    [PanelFleetSettingsCatalog.OverviewRefreshIntervalKey] = JsonSerializer.SerializeToElement(120),
                    [PanelFleetSettingsCatalog.TerminalEnabledKey] = JsonSerializer.SerializeToElement(false)
                }));

            var overview = new OverviewOptions();
            var resources = new ResourceOptions();
            var terminal = new TerminalOptions();
            using var provider = new ServiceCollection()
                .AddSingleton(terminal)
                .BuildServiceProvider();

            var runtime = new PanelFleetSettingsRuntime(
                seed,
                overview,
                resources,
                provider,
                TimeProvider.System,
                NullLogger<PanelFleetSettingsRuntime>.Instance);

            Assert.Equal(120, runtime.Current.OverviewRefreshIntervalSeconds);
            Assert.Equal(120, overview.RefreshIntervalSeconds);
            Assert.False(terminal.Enabled);
            Assert.Equal(3, runtime.Revision);
            Assert.Equal("stored", runtime.Sources[PanelFleetSettingsCatalog.OverviewRefreshIntervalKey]);

            PanelFleetSettingsChange? observed = null;
            runtime.Changed += change => observed = change;

            var updated = runtime.Update(runtime.Current with { ResourcesPageSize = 120 });

            Assert.NotNull(observed);
            Assert.Contains(PanelFleetSettingsCatalog.ResourcesPageSizeKey, updated.ChangedKeys);
            Assert.Equal(4, runtime.Revision);
            Assert.NotNull(seed.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
