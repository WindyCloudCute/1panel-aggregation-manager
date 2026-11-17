using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.Containers;

/// <summary>P6 容器模块 DI 扩展：只读服务 + 容器/Compose 写操作处理器。</summary>
public static class ContainerModule
{
    /// <summary>
    /// 注册容器模块。由组合根（Program.cs）调用一次；测试可重复调用前先 RemoveAll 相关服务。
    /// </summary>
    public static IServiceCollection AddContainerModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 幂等：生产组合根与集成测试 ConfigureTestServices 都可能调用本扩展；
        // 已经注册过容器模块时直接返回，避免 OperationHandlerRegistry 因重复 Kind 启动失败。
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ContainerOptions)))
        {
            return services;
        }

        services.AddSingleton(new ContainerOptions());
        services.AddScoped<IContainerService, ContainerService>();
        services.AddScoped<IComposeContentService, ComposeContentService>();
        services.AddSingleton<IContainerLogClientFactory, ContainerLogClientFactory>();

        // 写操作处理器：进程内无状态单例，由 OperationHandlerRegistry 统一按 Kind 注册。
        services.AddSingleton<IOperationHandler, ContainerStartHandler>();
        services.AddSingleton<IOperationHandler, ContainerStopHandler>();
        services.AddSingleton<IOperationHandler, ContainerRestartHandler>();
        services.AddSingleton<IOperationHandler, ContainerPauseHandler>();
        services.AddSingleton<IOperationHandler, ContainerUnpauseHandler>();
        services.AddSingleton<IOperationHandler, ContainerDeleteHandler>();
        services.AddSingleton<IOperationHandler, ContainerRenameHandler>();
        services.AddSingleton<IOperationHandler, ComposeUpHandler>();
        services.AddSingleton<IOperationHandler, ComposeStopHandler>();
        services.AddSingleton<IOperationHandler, ComposeRestartHandler>();
        services.AddSingleton<IOperationHandler, ComposeDownHandler>();
        services.AddSingleton<IOperationHandler, ComposeCreateHandler>();
        services.AddSingleton<IOperationHandler, ComposeUpdateHandler>();
        services.AddSingleton<IOperationHandler, ComposeRebuildHandler>();
        services.AddSingleton<IOperationHandler, ComposeDeleteHandler>();

        return services;
    }
}
