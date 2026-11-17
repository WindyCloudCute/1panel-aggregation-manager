using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PanelFleet.Infrastructure.Persistence;

/// <summary>应用启动时应用迁移；迁移前自动备份 SQLite 文件。</summary>
public static class DatabaseInitializer
{
    private const int MaxBackups = 5;

    public static async Task InitializeAsync(
        PanelFleetDbContext dbContext,
        string databasePath,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken)
            .ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken)
            .ConfigureAwait(false);

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false)).ToArray();

        if (pending.Length > 0 && File.Exists(databasePath))
        {
            var backupPath = $"{databasePath}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(databasePath, backupPath, overwrite: true);
            logger.LogInformation("数据库迁移前备份：{BackupPath}", backupPath);
            CleanupBackups(databasePath);
        }

        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        if (pending.Length > 0)
        {
            logger.LogInformation("数据库迁移完成：{Migrations}", string.Join(", ", pending));
        }
    }

    private static void CleanupBackups(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var backups = Directory.GetFiles(directory, $"{Path.GetFileName(databasePath)}.bak-*")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var stale in backups.Skip(MaxBackups))
        {
            File.Delete(stale);
        }
    }
}
