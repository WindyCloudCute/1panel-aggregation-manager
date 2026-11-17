using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PanelFleet.Infrastructure.Persistence;

/// <summary>供 dotnet-ef 设计时使用，避免启动 Web 宿主。</summary>
public sealed class PanelFleetDbContextFactory : IDesignTimeDbContextFactory<PanelFleetDbContext>
{
    public PanelFleetDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PanelFleetDbContext>()
            .UseSqlite("Data Source=panelfleet-design.db")
            .Options;

        return new PanelFleetDbContext(options);
    }
}
