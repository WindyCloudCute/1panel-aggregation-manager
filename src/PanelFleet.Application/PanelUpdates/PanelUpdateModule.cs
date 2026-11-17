using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Operations;

namespace PanelFleet.Application.PanelUpdates;

/// <summary>P12 面板更新模块的依赖注入扩展（由 Host 接线调用）。</summary>
public static class PanelUpdateModule
{
    /// <summary>注册版本检查服务与 <c>panel.update</c> 写操作处理器。</summary>
    public static IServiceCollection AddPanelUpdateModule(this IServiceCollection services) =>
        AddPanelUpdateModule(services, configure: null);

    /// <summary>
    /// 注册面板更新模块，并允许接线处覆盖默认配置。
    /// <para>幂等：Host 已经注册过（生产接线）或测试重复注册时直接返回，避免重复的
    /// <see cref="IOperationHandler"/> 导致写内核注册表启动失败。</para>
    /// </summary>
    public static IServiceCollection AddPanelUpdateModule(
        this IServiceCollection services,
        Action<PanelUpdateOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(PanelUpdateOptions)))
        {
            return services;
        }

        var options = new PanelUpdateOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddScoped<IPanelUpdateService, PanelUpdateService>();
        services.AddSingleton<IOperationHandler, PanelUpdateOperationHandler>();
        return services;
    }
}
