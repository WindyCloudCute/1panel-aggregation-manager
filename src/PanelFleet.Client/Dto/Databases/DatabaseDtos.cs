using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Databases;

// P8 数据库 DTO：宽松解析；**不映射任何密码字段**（1Panel 列表/用户响应里 password 通常为空，
// 但该字段可能包含敏感值，本项目有意不接收、不序列化）。

/// <summary>数据库实例（GET api/v2/databases/db/list/{engine}）。</summary>
public sealed class DatabaseInstanceDto
{
    public long Id { get; set; }

    public string? Type { get; set; }

    public string? From { get; set; }

    public string? Database { get; set; }

    public string? Version { get; set; }

    public string? Address { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>库列表项（MySQL/PG/Mongo 统一宽松映射）。</summary>
public sealed class DatabaseDbDto
{
    public long Id { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public string? Name { get; set; }

    public string? From { get; set; }

    /// <summary>MySQL 所属实例名（mysqlName）。</summary>
    public string? MysqlName { get; set; }

    /// <summary>PostgreSQL 所属实例名（postgresqlName）。</summary>
    public string? PostgresqlName { get; set; }

    /// <summary>MongoDB 所属实例名（mongodbName）。</summary>
    public string? MongodbName { get; set; }

    public string? Format { get; set; }

    public string? Collation { get; set; }

    public string? Username { get; set; }

    public string? Permission { get; set; }

    public bool? IsDelete { get; set; }

    public string? Description { get; set; }

    public bool? SuperUser { get; set; }

    /// <summary>所属实例名（按引擎字段回退）。</summary>
    [JsonIgnore]
    public string? InstanceName => FirstNonEmpty(MysqlName, PostgresqlName, MongodbName);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

/// <summary>MySQL 用户（POST api/v2/databases/users/search）。不含密码。</summary>
public sealed class DatabaseUserDto
{
    public string? Username { get; set; }

    public string? Host { get; set; }

    public string? Description { get; set; }

    public bool? IsDelete { get; set; }
}

/// <summary>MySQL 授权（POST api/v2/databases/grants/search）。</summary>
public sealed class DatabaseGrantDto
{
    public string? Database { get; set; }

    public string? Username { get; set; }

    public string? Host { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>字符集与排序规则（POST api/v2/databases/format/options）。</summary>
public sealed class DatabaseFormatOptionDto
{
    public string? Format { get; set; }

    public List<string>? Collations { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>MySQL 运行状态（POST api/v2/databases/status，仅本地实例支持）。</summary>
public sealed class DatabaseStatusDto
{
    [JsonPropertyName("Threads_connected")]
    public string? ThreadsConnected { get; set; }

    [JsonPropertyName("Threads_running")]
    public string? ThreadsRunning { get; set; }

    [JsonPropertyName("Max_used_connections")]
    public string? MaxUsedConnections { get; set; }

    [JsonPropertyName("Questions")]
    public string? Questions { get; set; }

    [JsonPropertyName("Bytes_received")]
    public string? BytesReceived { get; set; }

    [JsonPropertyName("Bytes_sent")]
    public string? BytesSent { get; set; }

    [JsonPropertyName("Uptime")]
    public string? Uptime { get; set; }

    [JsonPropertyName("Aborted_connects")]
    public string? AbortedConnects { get; set; }

    [JsonPropertyName("Slow_queries")]
    public string? SlowQueries { get; set; }

    [JsonPropertyName("Open_tables")]
    public string? OpenTables { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>实例基础信息（POST api/v2/databases/common/info，仅本地实例支持；不映射密码）。</summary>
public sealed class DatabaseBaseInfoDto
{
    public string? Name { get; set; }

    public int Port { get; set; }

    public bool RemoteConn { get; set; }

    public string? ContainerName { get; set; }

    public string? MysqlKey { get; set; }
}

/// <summary>删除前关联资源检查（POST /databases/*/del/check）。</summary>
public sealed class DatabaseLinkedResourceDto
{
    public string? Type { get; set; }

    public string? Name { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

// ---- Redis ----

/// <summary>Redis 状态（POST api/v2/databases/redis/status）。</summary>
public sealed class RedisStatusDto
{
    [JsonPropertyName("tcp_port")]
    public string? TcpPort { get; set; }

    [JsonPropertyName("uptime_in_days")]
    public string? UptimeInDays { get; set; }

    [JsonPropertyName("connected_clients")]
    public string? ConnectedClients { get; set; }

    [JsonPropertyName("used_memory")]
    public string? UsedMemory { get; set; }

    [JsonPropertyName("used_memory_rss")]
    public string? UsedMemoryRss { get; set; }

    [JsonPropertyName("used_memory_peak")]
    public string? UsedMemoryPeak { get; set; }

    [JsonPropertyName("mem_fragmentation_ratio")]
    public string? MemFragmentationRatio { get; set; }

    [JsonPropertyName("total_connections_received")]
    public string? TotalConnectionsReceived { get; set; }

    [JsonPropertyName("total_commands_processed")]
    public string? TotalCommandsProcessed { get; set; }

    [JsonPropertyName("instantaneous_ops_per_sec")]
    public string? InstantaneousOpsPerSec { get; set; }

    [JsonPropertyName("keyspace_hits")]
    public string? KeyspaceHits { get; set; }

    [JsonPropertyName("keyspace_misses")]
    public string? KeyspaceMisses { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Redis 配置（POST api/v2/databases/redis/conf）。不映射 requirepass。</summary>
public sealed class RedisConfDto
{
    public string? Database { get; set; }

    public string? Name { get; set; }

    public long Port { get; set; }

    public string? ContainerName { get; set; }

    public string? Timeout { get; set; }

    public string? Maxclients { get; set; }

    public string? Maxmemory { get; set; }
}

/// <summary>Redis 持久化配置（POST api/v2/databases/redis/persistence/conf）。</summary>
public sealed class RedisPersistenceDto
{
    public string? Appendonly { get; set; }

    public string? Appendfsync { get; set; }

    public string? Save { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

// ---- 请求体 ----

/// <summary>库列表请求（SearchDBWithPage）：database 为实例名，orderBy/order 必填。</summary>
public sealed record DatabaseSearchRequest(
    int Page,
    int PageSize,
    string? Info,
    string Database,
    string OrderBy,
    string Order);

/// <summary>MySQL 创建库（MysqlDBCreate）；Password 需为 Base64（1Panel 后端解码）。</summary>
public sealed record MysqlCreateDatabaseRequest(
    string Name,
    string From,
    string Database,
    string Format,
    string Collation,
    string Username,
    [property: JsonPropertyName("password")] string PasswordBase64,
    string Permission,
    string Description);

/// <summary>PostgreSQL 创建库（PostgresqlDBCreate）；Password 需为 Base64。</summary>
public sealed record PostgresqlCreateDatabaseRequest(
    string Name,
    string From,
    string Database,
    string Format,
    string Username,
    [property: JsonPropertyName("password")] string PasswordBase64,
    bool SuperUser,
    string Description);

/// <summary>MongoDB 创建库（MongodbDBCreate）；Password 需为 Base64。</summary>
public sealed record MongodbCreateDatabaseRequest(
    string Name,
    string From,
    string Database,
    string Username,
    [property: JsonPropertyName("password")] string PasswordBase64,
    string Permission,
    string Description);

/// <summary>删除库（MysqlDBDelete / PostgresqlDBDelete / MongodbDBDelete）。</summary>
public sealed record DatabaseDeleteRequest(
    long Id,
    string Type,
    string Database,
    bool ForceDelete,
    bool DeleteBackup);

/// <summary>删除前检查（MysqlDBDeleteCheck 等）。</summary>
public sealed record DatabaseDeleteCheckRequest(long Id, string Type, string Database);

/// <summary>修改库描述（UpdateDescription）。</summary>
public sealed record DatabaseDescriptionRequest(long Id, string Description);

/// <summary>MySQL 用户搜索（MysqlUserSearch）。</summary>
public sealed record DatabaseUserSearchRequest(string Database);

/// <summary>MySQL 创建用户（MysqlUserCreate）；PasswordBase64 为 Base64。</summary>
public sealed record DatabaseUserCreateRequest(
    string Database,
    string Username,
    [property: JsonPropertyName("password")] string PasswordBase64,
    string Host,
    string Description,
    IReadOnlyList<string>? Dbs = null);

/// <summary>MySQL 删除用户（MysqlUserDelete）。</summary>
public sealed record DatabaseUserDeleteRequest(string Database, string Username, string Host);

/// <summary>MySQL 修改用户密码（MysqlUserPassword）；PasswordBase64 为 Base64。</summary>
public sealed record DatabaseUserPasswordRequest(
    string Database,
    string Username,
    string Host,
    [property: JsonPropertyName("password")] string PasswordBase64);

/// <summary>MySQL 授权/取消授权（MysqlGrantCreate / MysqlGrantDelete）。</summary>
public sealed record DatabaseGrantRequest(string Database, string Db, string Username, string Host);

/// <summary>Redis 配置更新（RedisConfUpdate）。</summary>
public sealed record RedisConfUpdateRequest(
    string DbType,
    string Database,
    string Timeout,
    string Maxclients,
    string Maxmemory);


