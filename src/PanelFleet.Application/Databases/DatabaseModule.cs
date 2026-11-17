using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Operations;

namespace PanelFleet.Application.Databases;

/// <summary>P8 数据库模块 DI 扩展：只读服务 + 9 个写操作处理器（幂等注册）。</summary>
public static class DatabaseModule
{
    /// <summary>
    /// 注册数据库模块。生产组合根与集成测试都可能调用；已注册时直接返回，
    /// 避免 OperationHandlerRegistry 因重复 Kind 启动失败。
    /// </summary>
    public static IServiceCollection AddDatabaseModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(DatabaseOptions)))
        {
            return services;
        }

        services.AddSingleton(new DatabaseOptions());
        services.AddScoped<IDatabaseService, DatabaseService>();

        services.AddSingleton<IOperationHandler, DatabaseCreateHandler>();
        services.AddSingleton<IOperationHandler, DatabaseDeleteHandler>();
        services.AddSingleton<IOperationHandler, DatabaseDescriptionUpdateHandler>();
        services.AddSingleton<IOperationHandler, DatabaseUserCreateHandler>();
        services.AddSingleton<IOperationHandler, DatabaseUserDeleteHandler>();
        services.AddSingleton<IOperationHandler, DatabaseUserPasswordHandler>();
        services.AddSingleton<IOperationHandler, DatabaseGrantHandler>();
        services.AddSingleton<IOperationHandler, DatabaseGrantRevokeHandler>();
        services.AddSingleton<IOperationHandler, RedisConfigUpdateHandler>();

        return services;
    }
}
