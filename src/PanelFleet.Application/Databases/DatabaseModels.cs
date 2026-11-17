using PanelFleet.Application.Operations;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Databases;

/// <summary>P8 支持的数据库引擎。</summary>
public enum DatabaseEngine
{
    MySql = 0,
    PostgreSql = 1,
    MongoDb = 2,
    Redis = 3
}

/// <summary>引擎名与显示名转换（与 1Panel 路由参数一致）。</summary>
public static class DatabaseEngines
{
    public static readonly IReadOnlyList<DatabaseEngine> All =
    [
        DatabaseEngine.MySql,
        DatabaseEngine.PostgreSql,
        DatabaseEngine.MongoDb,
        DatabaseEngine.Redis
    ];

    public static string Key(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.MySql => "mysql",
        DatabaseEngine.PostgreSql => "postgresql",
        DatabaseEngine.MongoDb => "mongodb",
        DatabaseEngine.Redis => "redis",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "未知数据库引擎。")
    };

    public static string DisplayName(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.MySql => "MySQL",
        DatabaseEngine.PostgreSql => "PostgreSQL",
        DatabaseEngine.MongoDb => "MongoDB",
        DatabaseEngine.Redis => "Redis",
        _ => engine.ToString()
    };

    public static bool TryParse(string? value, out DatabaseEngine engine)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "mysql" or "mariadb":
                engine = DatabaseEngine.MySql;
                return true;
            case "postgresql":
                engine = DatabaseEngine.PostgreSql;
                return true;
            case "mongodb":
                engine = DatabaseEngine.MongoDb;
                return true;
            case "redis":
                engine = DatabaseEngine.Redis;
                return true;
            default:
                engine = default;
                return false;
        }
    }

    public static DatabaseEngine Parse(string? value) =>
        TryParse(value, out var engine)
            ? engine
            : throw new OperationValidationException("数据库引擎不合法（支持 mysql/postgresql/mongodb/redis）。");

    /// <summary>是否为可建库/删库/管用户的关系/文档型数据库引擎。</summary>
    public static bool IsDatabaseEngine(DatabaseEngine engine) =>
        engine is DatabaseEngine.MySql or DatabaseEngine.PostgreSql or DatabaseEngine.MongoDb;
}

/// <summary>库列表中的一行。</summary>
public sealed record DatabaseDbView(
    Guid NodeId,
    string NodeName,
    DatabaseEngine Engine,
    string Instance,
    long Id,
    string Name,
    string From,
    string? Format,
    string? Collation,
    string? Description,
    bool SuperUser,
    DateTimeOffset? CreatedAt);

/// <summary>数据库实例及其库列表。</summary>
public sealed record DatabaseInstanceView(
    Guid NodeId,
    string NodeName,
    DatabaseEngine Engine,
    string Instance,
    long Id,
    string From,
    string Version,
    string Address,
    IReadOnlyList<DatabaseDbView> Databases,
    int DatabaseTotal,
    bool DatabasesTruncated,
    PanelError? Error);

/// <summary>单节点数据库采集结果。</summary>
public sealed record DatabaseNodeGroup(
    Guid NodeId,
    string NodeName,
    bool IsEnabled,
    IReadOnlyList<DatabaseInstanceView> Instances,
    IReadOnlyList<string> Warnings,
    PanelError? Error,
    TimeSpan Elapsed);

/// <summary>跨节点数据库概览（节点 → 实例 → 库）。</summary>
public sealed record DatabaseOverview(
    IReadOnlyList<DatabaseNodeGroup> Nodes,
    DateTimeOffset GeneratedAt);

/// <summary>只读列表结果（带错误，不抛异常）。</summary>
public sealed record DatabaseReadResult<T>(IReadOnlyList<T> Items, PanelError? Error)
{
    public static DatabaseReadResult<T> Empty { get; } = new([], null);
}

public sealed record DatabaseUserView(string Username, string Host, string? Description);

public sealed record DatabaseGrantView(string Db, string Username, string Host);

public sealed record DatabaseFormatView(string Format, IReadOnlyList<string> Collations);

public sealed record DatabaseLinkResourceView(string Type, string Name);

/// <summary>实例运行状态（仅本地/应用安装实例支持；远程实例返回 Supported=false）。</summary>
public sealed record DatabaseStatusView(
    bool Supported,
    string? UnsupportedReason,
    string? ThreadsConnected,
    string? ThreadsRunning,
    string? MaxUsedConnections,
    string? Uptime,
    string? Questions,
    string? BytesReceived,
    string? BytesSent,
    string? SlowQueries,
    PanelError? Error);

/// <summary>Redis 状态。</summary>
public sealed record DatabaseRedisStatusView(
    string? TcpPort,
    string? UptimeInDays,
    string? ConnectedClients,
    string? UsedMemory,
    string? UsedMemoryRss,
    string? UsedMemoryPeak,
    string? MemFragmentationRatio,
    string? TotalConnectionsReceived,
    string? TotalCommandsProcessed,
    string? InstantaneousOpsPerSec);

/// <summary>Redis 配置（不含 requirepass）。</summary>
public sealed record DatabaseRedisConfView(
    string? Timeout,
    string? Maxclients,
    string? Maxmemory,
    string? Appendonly,
    string? Appendfsync,
    string? Save);

/// <summary>Redis 实例信息（状态 + 配置；CLI 不可用时只展示连接信息）。</summary>
public sealed record DatabaseRedisView(
    Guid NodeId,
    string NodeName,
    string Instance,
    string From,
    bool CliAvailable,
    bool Supported,
    string? UnsupportedReason,
    DatabaseRedisStatusView? Status,
    DatabaseRedisConfView? Conf,
    PanelError? Error);

/// <summary>数据库操作仅支持单节点（由处理器校验）。</summary>
public sealed class DatabaseOperationRejectedException : Exception
{
    public DatabaseOperationRejectedException(string message)
        : base(message)
    {
    }
}

/// <summary>节点不存在或已禁用（Web 层映射为 404）。</summary>
public sealed class DatabaseNodeNotFoundException : Exception
{
    public DatabaseNodeNotFoundException(Guid nodeId)
        : base($"节点不存在或已禁用（{nodeId}）。")
    {
    }
}
