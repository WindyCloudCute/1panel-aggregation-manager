using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Operations;

namespace PanelFleet.Application.AppStore;

/// <summary>应用商店模块 DI 注册（由 Program/测试在应用服务注册之后调用，可重复调用）。</summary>
public static class AppStoreServiceCollectionExtensions
{
    public static IServiceCollection AddAppStoreModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<AppStoreService>();

        // 处理器无状态，注册为单例：内核的 OperationHandlerRegistry 是单例，且单元测试可直接实例化。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationHandler, AppInstallOperationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationHandler, AppUninstallOperationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationHandler, AppUpdateOperationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationHandler, AppBatchUpgradeOperationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationHandler, AppParamsUpdateOperationHandler>());

        return services;
    }
}
