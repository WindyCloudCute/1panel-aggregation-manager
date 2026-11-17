using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Operations;

namespace PanelFleet.Application.Cronjobs;

/// <summary>P9 计划任务模块 DI 扩展：只读服务 + 6 个写操作处理器。</summary>
public static class CronjobModule
{
    /// <summary>
    /// 注册计划任务模块。由组合根（Program.cs/Application.DependencyInjection）调用；
    /// 集成测试可能重复调用，已注册 <see cref="CronjobOptions"/> 时直接返回（幂等）。
    /// </summary>
    public static IServiceCollection AddCronjobModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(CronjobOptions)))
        {
            return services;
        }

        services.AddSingleton(new CronjobOptions());
        services.AddScoped<ICronjobService, CronjobService>();

        // 写操作处理器：进程内无状态单例，由 OperationHandlerRegistry 按 Kind 注册。
        services.AddSingleton<IOperationHandler, CronjobCreateHandler>();
        services.AddSingleton<IOperationHandler, CronjobUpdateHandler>();
        services.AddSingleton<IOperationHandler, CronjobDeleteHandler>();
        services.AddSingleton<IOperationHandler, CronjobEnableHandler>();
        services.AddSingleton<IOperationHandler, CronjobDisableHandler>();
        services.AddSingleton<IOperationHandler, CronjobRunNowHandler>();

        return services;
    }
}
