using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.AppStore;
using PanelFleet.Application.Containers;
using PanelFleet.Application.Cronjobs;
using PanelFleet.Application.Databases;
using PanelFleet.Application.Logs;
using PanelFleet.Application.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Application.Overview;
using PanelFleet.Application.PanelUpdates;
using PanelFleet.Application.Resources;
using PanelFleet.Application.Websites;

namespace PanelFleet.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPanelFleetApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IConnectionTester, ConnectionTester>();
        services.AddScoped<INodeService, NodeService>();

        // 聚合总览：缓存/广播/熔断/队列/引擎为单例，用例为 scoped（每轮刷新/每个电路）。
        services.AddSingleton<IOverviewCache, OverviewCache>();
        services.AddSingleton<IOverviewBroadcaster, OverviewBroadcaster>();
        services.AddSingleton<IOverviewRefreshQueue, OverviewRefreshQueue>();
        services.AddSingleton<INodeCircuitBreaker, NodeCircuitBreaker>();
        services.AddSingleton<OverviewRefreshGates>();
        services.AddSingleton<IFanoutEngine, FanoutEngine>();
        services.AddScoped<IOverviewService, OverviewService>();

        // P4 资源清单：缓存/闸门/访问记录/加载器为单例，查询服务为 scoped。
        services.AddSingleton<IResourceCache, ResourceCache>();
        services.AddSingleton<IResourceAccessTracker, ResourceAccessTracker>();
        services.AddSingleton<ResourceGates>();
        services.AddSingleton<IResourceLoader, WebsiteResourceLoader>();
        services.AddSingleton<IResourceLoader, AppResourceLoader>();
        services.AddSingleton<IResourceLoader, ContainerResourceLoader>();
        services.AddSingleton<IResourceLoader, DatabaseResourceLoader>();
        services.AddSingleton<IResourceLoader, CronjobResourceLoader>();
        services.AddScoped<IResourceQueryService, ResourceQueryService>();

        // P5 写操作内核：确认存储/守卫/历史/处理器注册表为单例，执行服务为 scoped。
        services.AddSingleton(new OperationOptions());
        services.AddSingleton<IConfirmationStore, ConfirmationStore>();
        services.AddSingleton<OperationGuards>();
        services.AddSingleton<OperationHandlerRegistry>();
        services.AddSingleton<IOperationHistoryStore>(serviceProvider =>
            new OperationHistoryStore(serviceProvider.GetRequiredService<OperationOptions>()));
        services.AddScoped<IWriteOperationService, WriteOperationService>();

        // P5 应用商店：只读服务 + 4 个写操作处理器（幂等注册）。
        services.AddAppStoreModule();

        // P6 容器管理：只读服务 + 容器/Compose 写操作处理器（幂等注册）。
        services.AddContainerModule();

        // P12 1Panel 更新：版本检查服务 + panel.update 处理器（幂等注册）。
        services.AddPanelUpdateModule();

        // P7-P10：网站管理 / 数据库管理 / 计划任务管理 / 系统日志（均幂等注册）。
        services.AddWebsiteModule();
        services.AddDatabaseModule();
        services.AddCronjobModule();
        services.AddLogModule();

        return services;
    }
}
