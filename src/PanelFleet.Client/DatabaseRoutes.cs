namespace PanelFleet.Client;

/// <summary>
/// P8 数据库相关 1Panel v2 路由（MySQL / PostgreSQL / MongoDB / Redis）。
/// <para>按引擎切换的写路由通过方法收敛，禁止在 Application/Web 层拼接 URL。</para>
/// </summary>
public static class DatabaseRoutes
{
    public const string MySql = "mysql";

    public const string PostgreSql = "postgresql";

    public const string MongoDb = "mongodb";

    public const string Redis = "redis";

    // ---- 实例与库列表（只读） ----

    /// <summary>数据库实例列表：GET api/v2/databases/db/list/{engine}（engine=mysql|postgresql|mongodb|redis）。</summary>
    public static string Instances(string engine) => $"api/v2/databases/db/list/{NormalizeEngine(engine)}";

    /// <summary>库下拉项：GET api/v2/databases/db/item/{engine}。</summary>
    public static string DbItems(string engine) => $"api/v2/databases/db/item/{NormalizeEngine(engine)}";

    /// <summary>库列表（POST）：MySQL 为 /databases/search，PG 为 /databases/pg/search，Mongo 为 /databases/mongodb/search。</summary>
    public static string Search(string engine) => NormalizeEngine(engine) switch
    {
        MySql => "api/v2/databases/search",
        PostgreSql => "api/v2/databases/pg/search",
        MongoDb => "api/v2/databases/mongodb/search",
        _ => throw UnsupportedEngine(engine)
    };

    // ---- 库写操作 ----

    /// <summary>创建库：POST /databases（mysql）、/databases/pg、/databases/mongodb。</summary>
    public static string Create(string engine) => NormalizeEngine(engine) switch
    {
        MySql => "api/v2/databases",
        PostgreSql => "api/v2/databases/pg",
        MongoDb => "api/v2/databases/mongodb",
        _ => throw UnsupportedEngine(engine)
    };

    /// <summary>删除库：POST /databases/del、/databases/pg/del、/databases/mongodb/del。</summary>
    public static string Delete(string engine) => NormalizeEngine(engine) switch
    {
        MySql => "api/v2/databases/del",
        PostgreSql => "api/v2/databases/pg/del",
        MongoDb => "api/v2/databases/mongodb/del",
        _ => throw UnsupportedEngine(engine)
    };

    /// <summary>删除前检查关联资源：POST /databases/del/check、/databases/pg/del/check、/databases/mongodb/del/check。</summary>
    public static string DeleteCheck(string engine) => NormalizeEngine(engine) switch
    {
        MySql => "api/v2/databases/del/check",
        PostgreSql => "api/v2/databases/pg/del/check",
        MongoDb => "api/v2/databases/mongodb/del/check",
        _ => throw UnsupportedEngine(engine)
    };

    /// <summary>修改库描述：POST /databases/description/update、/databases/pg/description、/databases/mongodb/description。</summary>
    public static string Description(string engine) => NormalizeEngine(engine) switch
    {
        MySql => "api/v2/databases/description/update",
        PostgreSql => "api/v2/databases/pg/description",
        MongoDb => "api/v2/databases/mongodb/description",
        _ => throw UnsupportedEngine(engine)
    };

    /// <summary>修改数据库密码（本地实例）：POST /databases/change/password 或 /databases/pg/password。</summary>
    public static string RootPassword(string engine) => NormalizeEngine(engine) switch
    {
        MySql => "api/v2/databases/change/password",
        PostgreSql => "api/v2/databases/pg/password",
        _ => throw UnsupportedEngine(engine)
    };

    // ---- MySQL 用户 / 授权（只读 + 写） ----

    public const string UsersSearch = "api/v2/databases/users/search";

    public const string UsersCreate = "api/v2/databases/users";

    public const string UsersDelete = "api/v2/databases/users/del";

    public const string UsersPassword = "api/v2/databases/users/password";

    public const string GrantsSearch = "api/v2/databases/grants/search";

    public const string GrantsSummary = "api/v2/databases/grants/summary";

    public const string GrantsCreate = "api/v2/databases/grants";

    public const string GrantsDelete = "api/v2/databases/grants/del";

    // ---- 实例能力（仅本地/应用安装实例支持） ----

    public const string FormatOptions = "api/v2/databases/format/options";

    public const string Status = "api/v2/databases/status";

    public const string RemoteAccess = "api/v2/databases/remote";

    public const string BaseInfo = "api/v2/databases/common/info";

    // ---- Redis（按能力） ----

    public const string RedisCliCheck = "api/v2/databases/redis/check";

    public const string RedisStatus = "api/v2/databases/redis/status";

    public const string RedisConf = "api/v2/databases/redis/conf";

    public const string RedisPersistenceConf = "api/v2/databases/redis/persistence/conf";

    public const string RedisConfUpdate = "api/v2/databases/redis/conf/update";

    /// <summary>规整引擎名；未知引擎直接抛异常（调用方应先用 <see cref="IsSupportedEngine"/> 判断）。</summary>
    public static string NormalizeEngine(string engine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engine);
        var value = engine.Trim().ToLowerInvariant();
        return value switch
        {
            MySql or "mariadb" => MySql,
            PostgreSql => PostgreSql,
            MongoDb => MongoDb,
            Redis => Redis,
            _ => throw UnsupportedEngine(engine)
        };
    }

    public static bool IsSupportedEngine(string? engine) =>
        engine is not null
        && engine.Trim().ToLowerInvariant() is MySql or "mariadb" or PostgreSql or MongoDb or Redis;

    /// <summary>数据库引擎的显示名。</summary>
    public static string DisplayName(string engine) => NormalizeEngine(engine) switch
    {
        MySql => "MySQL",
        PostgreSql => "PostgreSQL",
        MongoDb => "MongoDB",
        Redis => "Redis",
        _ => engine
    };

    private static ArgumentOutOfRangeException UnsupportedEngine(string engine) =>
        new(nameof(engine), engine, "不支持的数据库引擎（支持 mysql/postgresql/mongodb/redis）。");
}
