using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Client;

namespace PanelFleet.Application.Logs;

/// <summary>P10 系统日志模块 DI 扩展（由组合根接线调用）。</summary>
public static class LogModule
{
    /// <summary>注册日志查询服务与容器日志客户端工厂回退。</summary>
    public static IServiceCollection AddLogModule(this IServiceCollection services) =>
        AddLogModule(services, configure: null);

    /// <summary>
    /// 注册系统日志模块，并允许接线处覆盖默认配置。
    /// <para>幂等：生产组合根与集成测试 ConfigureTestServices 都可能调用，已注册时直接返回。</para>
    /// </summary>
    public static IServiceCollection AddLogModule(
        this IServiceCollection services,
        Action<LogOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(LogOptions)))
        {
            return services;
        }

        var options = new LogOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddScoped<ILogService, LogService>();

        // 容器日志复用 P6 的 SSE 域客户端；若容器模块尚未注册则补一个默认实现，保证本模块可独立使用。
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IContainerLogClientFactory)))
        {
            services.AddSingleton<IContainerLogClientFactory, ContainerLogClientFactory>();
        }

        return services;
    }
}
