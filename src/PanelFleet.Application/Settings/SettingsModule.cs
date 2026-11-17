using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Configuration;
using PanelFleet.Application.Operations;
using PanelFleet.Application.Overview;
using PanelFleet.Application.Resources;

namespace PanelFleet.Application.Settings;

/// <summary>
/// 设置模块注册：PanelFleet 自身设置（热更新 + 审计）、1Panel 面板设置只读聚合、以及 3 个设置类写操作处理器。
/// <para>Host 需在注册完 <see cref="OverviewOptions"/>、<see cref="ResourceOptions"/> 与 <see cref="TimeProvider"/> 之后调用。</para>
/// </summary>
public static class SettingsModule
{
    /// <summary>注册设置模块（默认选项）。</summary>
    public static IServiceCollection AddSettingsModule(this IServiceCollection services) =>
        AddSettingsModule(services, configure: null);

    /// <summary>注册设置模块，并允许覆盖选项。</summary>
    public static IServiceCollection AddSettingsModule(
        this IServiceCollection services,
        Action<SettingsModuleOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 幂等：重复注册会产生重复的 IOperationHandler，导致操作注册表启动即失败。
        if (services.Any(descriptor => descriptor.ServiceType == typeof(SettingsModuleOptions)))
        {
            return services;
        }

        var options = new SettingsModuleOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);

        services.AddSingleton<IPanelFleetSettingsStore>(provider => new JsonFilePanelFleetSettingsStore(
            provider.GetRequiredService<SettingsModuleOptions>().ResolveFilePath(),
            provider.GetRequiredService<ILogger<JsonFilePanelFleetSettingsStore>>()));

        services.AddSingleton<IPanelFleetSettingsRuntime>(provider => new PanelFleetSettingsRuntime(
            provider.GetRequiredService<IPanelFleetSettingsStore>(),
            provider.GetRequiredService<OverviewOptions>(),
            provider.GetRequiredService<ResourceOptions>(),
            provider,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<PanelFleetSettingsRuntime>>()));

        services.AddScoped<PanelFleetSettingsService>();
        services.AddScoped<PanelSettingsService>();

        services.AddSingleton<IOperationHandler, PanelSettingUpdateHandler>();
        services.AddSingleton<IOperationHandler, PanelApiConfigHandler>();
        services.AddSingleton<IOperationHandler, PanelPasswordUpdateHandler>();

        return services;
    }
}
