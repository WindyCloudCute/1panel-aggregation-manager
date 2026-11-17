using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Databases;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Databases;

/// <summary>数据库写操作参数解析与校验规则（名称/密码/单节点限制）。</summary>
internal static class DatabaseOperationRules
{
    private static readonly Regex InstanceNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MysqlDatabasePattern =
        new("^[A-Za-z0-9_][A-Za-z0-9_$-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PostgresqlDatabasePattern =
        new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UserNamePattern =
        new("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HostPattern =
        new("^[A-Za-z0-9%._:/-]{1,255}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] MongoForbidden =
    [
        "/", "\\", ".", "\"", "$", "*", "<", ">", ":", "|", "?", " ", "\t"
    ];

    public static DatabaseEngine RequireDatabaseEngine(OperationParameters parameters)
    {
        var engine = DatabaseEngines.Parse(parameters.RequireString("engine", "数据库引擎"));
        if (!DatabaseEngines.IsDatabaseEngine(engine))
        {
            throw new OperationValidationException("数据库操作仅支持 MySQL / PostgreSQL / MongoDB（Redis 仅支持配置更新）。");
        }

        return engine;
    }

    public static string RequireInstance(OperationParameters parameters) =>
        RequireName(parameters, "instance", "实例名称", InstanceNamePattern, 64);

    public static string RequireDatabaseName(OperationParameters parameters, DatabaseEngine engine, string key = "database")
    {
        var name = parameters.RequireString(key, "数据库名称").Trim();
        var valid = engine switch
        {
            DatabaseEngine.MySql => MysqlDatabasePattern.IsMatch(name),
            DatabaseEngine.PostgreSql => PostgresqlDatabasePattern.IsMatch(name),
            DatabaseEngine.MongoDb => IsValidMongoName(name),
            _ => false
        };

        if (!valid)
        {
            throw new OperationValidationException($"数据库名称不合法：{Truncate(name, 64)}。");
        }

        return name;
    }

    public static string RequireUserName(OperationParameters parameters) =>
        RequireName(parameters, "username", "用户名", UserNamePattern, 32);

    public static string RequireHost(OperationParameters parameters, string fallback = "%")
    {
        var host = parameters.GetString("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            return fallback;
        }

        host = host.Trim();
        if (!HostPattern.IsMatch(host))
        {
            throw new OperationValidationException($"主机/访问范围不合法：{Truncate(host, 64)}。");
        }

        return host;
    }

    public static string RequirePassword(OperationParameters parameters, string key = "password")
    {
        var password = parameters.GetString(key);
        if (string.IsNullOrEmpty(password))
        {
            throw OperationValidationException.Missing("数据库密码");
        }

        if (password.Length > 128)
        {
            throw new OperationValidationException("数据库密码长度不能超过 128 个字符。");
        }

        return password;
    }

    public static long RequireId(OperationParameters parameters)
    {
        var id = parameters.GetInt64("id");
        if (id is null or <= 0)
        {
            throw new OperationValidationException("缺少有效的数据库记录 id。");
        }

        return id.Value;
    }

    public static string RequireFrom(OperationParameters parameters)
    {
        var from = parameters.GetString("from");
        var value = string.IsNullOrWhiteSpace(from) ? "remote" : from.Trim().ToLowerInvariant();
        if (value is not ("local" or "remote"))
        {
            throw new OperationValidationException("实例来源 from 必须为 local 或 remote。");
        }

        return value;
    }

    public static string ToBase64(string plainText) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

    public static void EnsureSingleNode(OperationPlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Nodes.Count == 0)
        {
            throw new OperationValidationException("至少选择一个节点。");
        }

        if (context.Nodes.Count > 1)
        {
            throw new OperationValidationException("数据库写操作仅支持单节点执行，请逐节点操作。");
        }
    }

    public static OperationPlan BuildPlan(
        OperationPlanContext context,
        string title,
        string summary,
        OperationRisk risk,
        IReadOnlyList<string> warnings,
        string? typedConfirmationText)
    {
        var node = context.Nodes[0];
        return new OperationPlan(
            title,
            summary,
            risk,
            [new OperationTarget(node.Id, node.Name)],
            warnings,
            typedConfirmationText,
            "database");
    }

    public static OperationPlan BuildPlan(
        OperationPlanContext context,
        string title,
        string summary,
        OperationRisk risk,
        string warning,
        string? typedConfirmationText) =>
        BuildPlan(context, title, summary, risk, [warning], typedConfirmationText);

    public static async Task<OperationStepResult> PostAsync(
        OperationNodeContext context,
        string relativePath,
        object body,
        string successMessage,
        string? detailJson,
        string? targetId,
        CancellationToken cancellationToken)
    {
        var result = await context.Client
            .PostAsync<JsonElement>(relativePath, body, cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                "1Panel 拒绝执行：" + result.Error.Message,
                null,
                null,
                result.Error,
                result.Elapsed,
                targetId);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            successMessage,
            null,
            detailJson,
            null,
            result.Elapsed,
            targetId);
    }

    public static string Detail(string operation, DatabaseEngine engine, string instance, string? database, string? user = null)
    {
        var detail = new JsonObject
        {
            ["operation"] = operation,
            ["engine"] = DatabaseEngines.Key(engine),
            ["instance"] = instance
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            detail["database"] = database;
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            detail["user"] = user;
        }

        return detail.ToJsonString();
    }

    private static string RequireName(
        OperationParameters parameters,
        string key,
        string label,
        Regex pattern,
        int maxLength)
    {
        var value = parameters.RequireString(key, label).Trim();
        if (value.Length > maxLength || !pattern.IsMatch(value))
        {
            throw new OperationValidationException($"{label}不合法：{Truncate(value, 64)}。");
        }

        return value;
    }

    private static bool IsValidMongoName(string name)
    {
        if (name.Length is 0 or > 63)
        {
            return false;
        }

        if (name.Any(char.IsControl))
        {
            return false;
        }

        return !MongoForbidden.Any(token => name.Contains(token, StringComparison.Ordinal));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

/// <summary>创建数据库（database.create，Medium）。</summary>
public sealed class DatabaseCreateHandler : IOperationHandler
{
    public string Kind => "database.create";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var engine = DatabaseOperationRules.RequireDatabaseEngine(context.Parameters);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var name = DatabaseOperationRules.RequireDatabaseName(context.Parameters, engine, "name");
        var username = context.Parameters.GetString("username")?.Trim();
        var password = context.Parameters.GetString("password");
        var description = context.Parameters.GetString("description")?.Trim() ?? string.Empty;
        var summary = $"在节点 {context.Nodes[0].Name} 的 {DatabaseEngines.DisplayName(engine)} 实例 {instance} 上创建数据库 {name}。";

        if (engine is DatabaseEngine.MySql or DatabaseEngine.PostgreSql)
        {
            if (engine == DatabaseEngine.PostgreSql && string.IsNullOrWhiteSpace(username))
            {
                throw OperationValidationException.Missing("数据库用户名（username）");
            }

            if (!string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
            {
                throw OperationValidationException.Missing("数据库用户密码（password）");
            }
        }
        else
        {
            DatabaseOperationRules.RequireUserName(context.Parameters);
            DatabaseOperationRules.RequirePassword(context.Parameters);
            RequireMongoPermission(context.Parameters);
        }

        if (description.Length > 256)
        {
            throw new OperationValidationException("数据库描述不能超过 256 个字符。");
        }

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "创建数据库",
            summary,
            OperationRisk.Medium,
            [
                "创建数据库会在实例上立即执行 DDL，请确认实例、字符集与排序规则。",
                "如需同时创建数据库账号，请确认权限范围符合最小权限原则；密码只用于请求传输，不写入审计或日志。"
            ],
            null));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = DatabaseOperationRules.RequireDatabaseEngine(context.Parameters);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var name = DatabaseOperationRules.RequireDatabaseName(context.Parameters, engine, "name");
        var from = DatabaseOperationRules.RequireFrom(context.Parameters);
        var description = context.Parameters.GetString("description")?.Trim() ?? string.Empty;
        var username = context.Parameters.GetString("username")?.Trim() ?? string.Empty;
        var password = context.Parameters.GetString("password") ?? string.Empty;

        object body = engine switch
        {
            DatabaseEngine.MySql => new MysqlCreateDatabaseRequest(
                name,
                from,
                instance,
                DefaultIfEmpty(context.Parameters.GetString("format"), "utf8mb4"),
                context.Parameters.GetString("collation")?.Trim() ?? string.Empty,
                username,
                string.IsNullOrEmpty(password) ? string.Empty : DatabaseOperationRules.ToBase64(password),
                DefaultIfEmpty(context.Parameters.GetString("permission"), "%"),
                description),
            DatabaseEngine.PostgreSql => new PostgresqlCreateDatabaseRequest(
                name,
                from,
                instance,
                DefaultIfEmpty(context.Parameters.GetString("format"), "utf8"),
                username,
                DatabaseOperationRules.ToBase64(DatabaseOperationRules.RequirePassword(context.Parameters)),
                context.Parameters.GetBoolean("superUser"),
                description),
            DatabaseEngine.MongoDb => new MongodbCreateDatabaseRequest(
                name,
                from,
                instance,
                DatabaseOperationRules.RequireUserName(context.Parameters),
                DatabaseOperationRules.ToBase64(DatabaseOperationRules.RequirePassword(context.Parameters)),
                RequireMongoPermission(context.Parameters),
                description),
            _ => throw new OperationValidationException("该引擎不支持创建数据库。")
        };

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.Create(DatabaseEngines.Key(engine)),
                body,
                $"数据库 {name} 已提交创建（1Panel 同步执行，请刷新确认）。",
                DatabaseOperationRules.Detail("create", engine, instance, name, string.IsNullOrWhiteSpace(username) ? null : username),
                name,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string RequireMongoPermission(OperationParameters parameters)
    {
        var permission = DefaultIfEmpty(parameters.GetString("permission"), "readWrite");
        return permission is "dbOwner" or "read" or "readWrite" or "userAdmin"
            ? permission
            : throw new OperationValidationException("MongoDB 权限必须为 dbOwner/read/readWrite/userAdmin。");
    }

    private static string DefaultIfEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

/// <summary>删除数据库（database.delete，Critical，强确认文本=库名）。</summary>
public sealed class DatabaseDeleteHandler : IOperationHandler
{
    public string Kind => "database.delete";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var engine = DatabaseOperationRules.RequireDatabaseEngine(context.Parameters);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var name = DatabaseOperationRules.RequireDatabaseName(context.Parameters, engine);
        var id = DatabaseOperationRules.RequireId(context.Parameters);

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "删除数据库",
            $"在节点 {context.Nodes[0].Name} 的 {DatabaseEngines.DisplayName(engine)} 实例 {instance} 上删除数据库 {name}（id={id.ToString(CultureInfo.InvariantCulture)}）。",
            OperationRisk.Critical,
            [
                "删除数据库会永久删除库内全部数据与授权关系，无法恢复。",
                "若库被网站或应用使用，删除后相关站点/应用会立即不可用。",
                "如需同时删除关联备份，请在下一次确认中明确勾选；删除操作逐节点执行且不回滚。"
            ],
            name));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = DatabaseOperationRules.RequireDatabaseEngine(context.Parameters);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var name = DatabaseOperationRules.RequireDatabaseName(context.Parameters, engine);
        var id = DatabaseOperationRules.RequireId(context.Parameters);

        var body = new DatabaseDeleteRequest(
            id,
            DatabaseEngines.Key(engine),
            instance,
            context.Parameters.GetBoolean("forceDelete"),
            context.Parameters.GetBoolean("deleteBackup"));

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.Delete(DatabaseEngines.Key(engine)),
                body,
                $"数据库 {name} 已提交删除（1Panel 同步执行，请刷新确认）。",
                DatabaseOperationRules.Detail("delete", engine, instance, name),
                name,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>修改数据库描述（database.description_update，Medium）。</summary>
public sealed class DatabaseDescriptionUpdateHandler : IOperationHandler
{
    public string Kind => "database.description_update";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var engine = DatabaseOperationRules.RequireDatabaseEngine(context.Parameters);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var id = DatabaseOperationRules.RequireId(context.Parameters);
        var description = context.Parameters.GetString("description")?.Trim() ?? string.Empty;

        if (description.Length > 256)
        {
            throw new OperationValidationException("数据库描述不能超过 256 个字符。");
        }

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "修改数据库描述",
            $"在节点 {context.Nodes[0].Name} 的 {DatabaseEngines.DisplayName(engine)} 实例 {instance} 上修改数据库记录（id={id.ToString(CultureInfo.InvariantCulture)}）的描述。",
            OperationRisk.Medium,
            "描述仅用于 1Panel 展示，不影响库内数据；请确认修改的是目标数据库记录。",
            null));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = DatabaseOperationRules.RequireDatabaseEngine(context.Parameters);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var id = DatabaseOperationRules.RequireId(context.Parameters);
        var description = context.Parameters.GetString("description")?.Trim() ?? string.Empty;

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.Description(DatabaseEngines.Key(engine)),
                new DatabaseDescriptionRequest(id, description),
                "数据库描述已更新。",
                DatabaseOperationRules.Detail("description_update", engine, instance, null),
                null,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>创建数据库用户（database.user_create，High，MySQL）。</summary>
public sealed class DatabaseUserCreateHandler : IOperationHandler
{
    public string Kind => "database.user_create";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);
        DatabaseOperationRules.RequirePassword(context.Parameters);
        var dbs = context.Parameters.GetStringArray("dbs");
        var description = context.Parameters.GetString("description")?.Trim() ?? string.Empty;

        if (description.Length > 256)
        {
            throw new OperationValidationException("用户描述不能超过 256 个字符。");
        }

        var dbText = dbs.Count == 0 ? "不授予库权限" : "授予 " + string.Join("、", dbs) + " 的访问权限";
        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "创建数据库用户",
            $"在节点 {context.Nodes[0].Name} 的 MySQL 实例 {instance} 上创建用户 {username}@{host}，{dbText}。",
            OperationRisk.High,
            [
                "创建数据库账号会立即生效；请确认账号名、访问范围与库授权符合最小权限原则。",
                "密码只用于请求传输，不写入审计或日志；请通过安全渠道交付密码。"
            ],
            username));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);
        var password = DatabaseOperationRules.RequirePassword(context.Parameters);
        var dbs = context.Parameters.GetStringArray("dbs");

        var body = new DatabaseUserCreateRequest(
            instance,
            username,
            DatabaseOperationRules.ToBase64(password),
            host,
            context.Parameters.GetString("description")?.Trim() ?? string.Empty,
            dbs.Count == 0 ? null : dbs);

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.UsersCreate,
                body,
                $"用户 {username}@{host} 已创建并应用授权。",
                DatabaseOperationRules.Detail("user_create", DatabaseEngine.MySql, instance, null, username),
                username,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>删除数据库用户（database.user_delete，High，MySQL）。</summary>
public sealed class DatabaseUserDeleteHandler : IOperationHandler
{
    public string Kind => "database.user_delete";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "删除数据库用户",
            $"在节点 {context.Nodes[0].Name} 的 MySQL 实例 {instance} 上删除用户 {username}@{host}。",
            OperationRisk.High,
            [
                "删除账号后使用该账号连接数据库的应用/网站会立即断开，且不可恢复。",
                "该账号在所有库上的授权会一并清除。"
            ],
            username));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.UsersDelete,
                new DatabaseUserDeleteRequest(instance, username, host),
                $"用户 {username}@{host} 已删除。",
                DatabaseOperationRules.Detail("user_delete", DatabaseEngine.MySql, instance, null, username),
                username,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>修改数据库用户密码（database.user_password，High，MySQL）。</summary>
public sealed class DatabaseUserPasswordHandler : IOperationHandler
{
    public string Kind => "database.user_password";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);
        DatabaseOperationRules.RequirePassword(context.Parameters);

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "修改数据库用户密码",
            $"在节点 {context.Nodes[0].Name} 的 MySQL 实例 {instance} 上重置用户 {username}@{host} 的密码。",
            OperationRisk.High,
            [
                "修改密码后使用旧密码的应用/网站会立即失去数据库连接。",
                "密码只用于请求传输，不写入审计或日志；请通过安全渠道交付新密码。"
            ],
            username));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);
        var password = DatabaseOperationRules.RequirePassword(context.Parameters);

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.UsersPassword,
                new DatabaseUserPasswordRequest(
                    instance,
                    username,
                    host,
                    DatabaseOperationRules.ToBase64(password)),
                $"用户 {username}@{host} 密码已重置。",
                DatabaseOperationRules.Detail("user_password", DatabaseEngine.MySql, instance, null, username),
                username,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>授予数据库用户访问权限（database.grant，High，MySQL）。</summary>
public sealed class DatabaseGrantHandler : IOperationHandler
{
    public string Kind => "database.grant";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var database = DatabaseOperationRules.RequireDatabaseName(context.Parameters, DatabaseEngine.MySql, "db");
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "授予数据库权限",
            $"在节点 {context.Nodes[0].Name} 的 MySQL 实例 {instance} 上授予 {username}@{host} 对数据库 {database} 的访问权限。",
            OperationRisk.High,
            [
                "授权变更立即生效，可能扩大账号的访问范围。",
                "请确认账号与目标库匹配，并遵循最小权限原则。"
            ],
            database));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var database = DatabaseOperationRules.RequireDatabaseName(context.Parameters, DatabaseEngine.MySql, "db");
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.GrantsCreate,
                new DatabaseGrantRequest(instance, database, username, host),
                $"已授予 {username}@{host} 对 {database} 的权限。",
                DatabaseOperationRules.Detail("grant", DatabaseEngine.MySql, instance, database, username),
                database,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>取消数据库用户访问权限（database.grant_revoke，High，MySQL）。</summary>
public sealed class DatabaseGrantRevokeHandler : IOperationHandler
{
    public string Kind => "database.grant_revoke";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var database = DatabaseOperationRules.RequireDatabaseName(context.Parameters, DatabaseEngine.MySql, "db");
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "取消数据库权限",
            $"在节点 {context.Nodes[0].Name} 的 MySQL 实例 {instance} 上取消 {username}@{host} 对数据库 {database} 的访问权限。",
            OperationRisk.High,
            [
                "取消授权后使用该账号访问此库的应用/网站会立即失败。",
                "授权变更不可自动回滚，需要时可重新授予。"
            ],
            database));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var database = DatabaseOperationRules.RequireDatabaseName(context.Parameters, DatabaseEngine.MySql, "db");
        var username = DatabaseOperationRules.RequireUserName(context.Parameters);
        var host = DatabaseOperationRules.RequireHost(context.Parameters);

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.GrantsDelete,
                new DatabaseGrantRequest(instance, database, username, host),
                $"已取消 {username}@{host} 对 {database} 的权限。",
                DatabaseOperationRules.Detail("grant_revoke", DatabaseEngine.MySql, instance, database, username),
                database,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>更新 Redis 配置（redis.config_update，High；仅本地实例）。</summary>
public sealed class RedisConfigUpdateHandler : IOperationHandler
{
    public string Kind => "redis.config_update";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        DatabaseOperationRules.EnsureSingleNode(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var timeout = context.Parameters.GetString("timeout")?.Trim();
        var maxclients = context.Parameters.GetString("maxclients")?.Trim();
        var maxmemory = context.Parameters.GetString("maxmemory")?.Trim();

        if (string.IsNullOrWhiteSpace(timeout) && string.IsNullOrWhiteSpace(maxclients) && string.IsNullOrWhiteSpace(maxmemory))
        {
            throw new OperationValidationException("至少提供一个 Redis 配置项（timeout/maxclients/maxmemory）。");
        }

        ValidateNumber(timeout, "timeout");
        ValidateNumber(maxclients, "maxclients");
        ValidateNumber(maxmemory, "maxmemory");

        return Task.FromResult(DatabaseOperationRules.BuildPlan(
            context,
            "更新 Redis 配置",
            $"在节点 {context.Nodes[0].Name} 上更新 Redis 实例 {instance} 的配置。",
            OperationRisk.High,
            [
                "修改 Redis 配置会重载服务，可能导致短暂连接中断。",
                "maxmemory 设置过小可能触发键淘汰（取决于淘汰策略）。",
                "仅本地/应用安装的 Redis 实例支持配置更新。"
            ],
            instance));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var instance = DatabaseOperationRules.RequireInstance(context.Parameters);
        var dbType = context.Parameters.GetString("dbType")?.Trim().ToLowerInvariant() ?? "redis";
        if (dbType is not ("redis" or "redis-cluster"))
        {
            throw new OperationValidationException("Redis 类型必须为 redis 或 redis-cluster。");
        }

        var timeout = context.Parameters.GetString("timeout")?.Trim() ?? string.Empty;
        var maxclients = context.Parameters.GetString("maxclients")?.Trim() ?? string.Empty;
        var maxmemory = context.Parameters.GetString("maxmemory")?.Trim() ?? string.Empty;
        ValidateNumber(timeout, "timeout");
        ValidateNumber(maxclients, "maxclients");
        ValidateNumber(maxmemory, "maxmemory");

        return await DatabaseOperationRules.PostAsync(
                context,
                DatabaseRoutes.RedisConfUpdate,
                new RedisConfUpdateRequest(dbType, instance, timeout, maxclients, maxmemory),
                $"Redis 实例 {instance} 配置已更新。",
                DatabaseOperationRules.Detail("redis_config_update", DatabaseEngine.Redis, instance, null),
                instance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateNumber(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            throw new OperationValidationException($"Redis 配置 {label} 必须为非负整数。");
        }
    }
}

