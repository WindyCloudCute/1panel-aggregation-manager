using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Terminal;

namespace PanelFleet.Web.Terminal;

/// <summary>终端模块 DI 接线（由 Program 调用一次；重复调用幂等）。</summary>
public static class TerminalModule
{
    /// <summary>
    /// 注册终端配置与服务。幂等：已注册 <see cref="TerminalOptions"/> 时直接返回。
    /// </summary>
    public static IServiceCollection AddTerminalModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(TerminalOptions)))
        {
            return services;
        }

        var options = configuration.GetSection("Terminal").Get<TerminalOptions>() ?? new TerminalOptions();
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton<TerminalAuthCache>();
        services.TryAddSingleton<TerminalSessionRegistry>();
        services.TryAddSingleton<ITerminalBackend, PanelTerminalBackend>();
        services.TryAddScoped<ITerminalSessionManager, TerminalSessionManager>();
        return services;
    }
}
