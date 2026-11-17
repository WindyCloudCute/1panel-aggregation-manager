using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Infrastructure.Persistence;
using PanelFleet.Infrastructure.Repositories;
using PanelFleet.Infrastructure.Security;

namespace PanelFleet.Infrastructure;

public sealed record PanelFleetDataPaths(string DataDirectory, string DatabasePath);

public static class DependencyInjection
{
    public static IServiceCollection AddPanelFleetInfrastructure(
        this IServiceCollection services,
        string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "panel.db");

        services.AddDbContext<PanelFleetDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));

        services.AddScoped<INodeStore, NodeStore>();
        services.AddScoped<INodeGroupStore, NodeGroupStore>();
        services.AddScoped<IAuditStore, AuditStore>();
        services.AddSingleton<INodeSecretProtector, DataProtectionNodeSecretProtector>();
        services.AddSingleton(new PanelFleetDataPaths(dataDirectory, databasePath));

        return services;
    }
}
