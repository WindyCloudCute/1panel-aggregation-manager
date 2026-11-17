using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Operations;

namespace PanelFleet.Application.Websites;

/// <summary>P7 网站模块 DI 扩展：只读服务 + 网站/域名/反向代理/SSL 写操作处理器。</summary>
public static class WebsiteModule
{
    /// <summary>
    /// 注册网站管理模块。由组合根（Program.cs）调用一次；集成测试可安全重复调用（已注册则直接返回）。
    /// </summary>
    public static IServiceCollection AddWebsiteModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(WebsiteOptions)))
        {
            return services;
        }

        services.AddSingleton(new WebsiteOptions());
        services.AddScoped<WebsiteService>();

        // 写操作处理器：进程内无状态单例，由 OperationHandlerRegistry 按 Kind 统一注册。
        services.AddSingleton<IOperationHandler, WebsiteStartHandler>();
        services.AddSingleton<IOperationHandler, WebsiteStopHandler>();
        services.AddSingleton<IOperationHandler, WebsiteUpdateHandler>();
        services.AddSingleton<IOperationHandler, WebsitePhpVersionHandler>();
        services.AddSingleton<IOperationHandler, DomainCreateHandler>();
        services.AddSingleton<IOperationHandler, DomainUpdateHandler>();
        services.AddSingleton<IOperationHandler, DomainDeleteHandler>();
        services.AddSingleton<IOperationHandler, ProxyCreateHandler>();
        services.AddSingleton<IOperationHandler, ProxyUpdateHandler>();
        services.AddSingleton<IOperationHandler, ProxyDeleteHandler>();
        services.AddSingleton<IOperationHandler, ProxyStatusHandler>();
        services.AddSingleton<IOperationHandler, SslApplyHandler>();
        services.AddSingleton<IOperationHandler, SslDeployHandler>();

        // WEBSITE+：建站、设主域名、创建/申请证书、关闭 HTTPS。
        services.AddSingleton<IOperationHandler, WebsiteCreateHandler>();
        services.AddSingleton<IOperationHandler, DomainPrimaryHandler>();
        services.AddSingleton<IOperationHandler, SslRequestHandler>();
        services.AddSingleton<IOperationHandler, SslDisableHandler>();

        return services;
    }
}
