using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Cronjobs;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Cronjobs;

/// <summary>
/// JSON 节点数值/字符串读取（兼容 int/long/double/string 等不同写入方式，
/// 因为前端 JSON 的整数在不同路径可能是 int 或 long）。
/// </summary>
internal static class CronjobJson
{
    public static long? ToLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<int>(out var intNumber))
        {
            return intNumber;
        }

        if (value.TryGetValue<double>(out var doubleNumber)
            && doubleNumber <= long.MaxValue
            && doubleNumber >= long.MinValue)
        {
            return (long)doubleNumber;
        }

        if (value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromText))
        {
            return fromText;
        }

        return long.TryParse(value.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromRaw)
            ? fromRaw
            : null;
    }

    public static string? ToStringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool ToBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed;
    }
}

/// <summary>
/// 批量目标解析：<c>cronjobsByNode</c>（节点 guid → 任务 id 数组）、<c>ids</c>（id 数组）、<c>cronjob</c>（单个 id）。
/// 与 P6 容器模块的 targets 解析同构。
/// </summary>
public static class CronjobTargets
{
    public const string ByNodeKey = "cronjobsByNode";

    public const string IdsKey = "ids";

    public const string SingleKey = "cronjob";

    /// <summary>解析节点 → 任务 id 列表；批量引用未知节点时报错（执行阶段可忽略）。</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<long>> Parse(
        OperationParameters parameters,
        IReadOnlyList<NodeRecord> nodes,
        int maxBatch,
        bool ignoreUnknownNodes = false)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Count == 0)
        {
            throw new OperationValidationException("至少选择一个节点。");
        }

        var nodeIds = nodes.Select(node => node.Id.Value).ToHashSet();
        var map = new Dictionary<Guid, IReadOnlyList<long>>();

        if (parameters.GetObject(ByNodeKey) is { } byNode)
        {
            foreach (var pair in byNode)
            {
                if (!Guid.TryParse(pair.Key, out var nodeId) || !nodeIds.Contains(nodeId))
                {
                    if (ignoreUnknownNodes)
                    {
                        continue;
                    }

                    throw new OperationValidationException($"参数包含未选择的节点：{pair.Key}。");
                }

                var ids = ReadIds(pair.Value);
                if (ids.Count == 0)
                {
                    throw new OperationValidationException($"节点 {pair.Key} 未指定计划任务。");
                }

                map[nodeId] = ids;
            }

            foreach (var node in nodes)
            {
                if (!map.ContainsKey(node.Id.Value))
                {
                    throw new OperationValidationException($"节点 {node.Name} 未指定计划任务。");
                }
            }
        }
        else
        {
            var single = parameters.Raw.TryGetPropertyValue(SingleKey, out var singleNode)
                ? CronjobJson.ToLong(singleNode)
                : null;
            var ids = single is > 0
                ? [single.Value]
                : ReadIds(parameters.GetArray(IdsKey));
            if (ids.Count == 0)
            {
                throw OperationValidationException.Missing("计划任务（cronjob 或 cronjobsByNode）");
            }

            foreach (var node in nodes)
            {
                map[node.Id.Value] = ids;
            }
        }

        var total = map.Values.Sum(ids => ids.Count);
        if (total > maxBatch)
        {
            throw new OperationValidationException($"单次最多操作 {maxBatch} 个计划任务。");
        }

        return map;
    }

    private static IReadOnlyList<long> ReadIds(JsonNode? node)
    {
        var ids = new List<long>();
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    AddId(ids, item);
                }

                break;
            case JsonValue value:
                AddId(ids, value);
                break;
        }

        return ids;
    }

    private static void AddId(List<long> ids, JsonNode? node)
    {
        if (node is null)
        {
            return;
        }

        var parsed = CronjobJson.ToLong(node);
        if (parsed is null or <= 0)
        {
            throw new OperationValidationException("计划任务 id 不合法。");
        }

        if (!ids.Contains(parsed.Value))
        {
            ids.Add(parsed.Value);
        }
    }
}

/// <summary>计划任务创建/更新载荷的解析与校验。</summary>
public static partial class CronjobPayload
{
    public const string PayloadKey = "payload";

    private static readonly string[] DatabaseTypes =
        ["mysql", "mariadb", "mysql-cluster", "postgresql", "postgresql-cluster", "mongodb"];

    /// <summary>读取 <c>payload</c> 对象（缺失报错）。</summary>
    public static JsonObject Require(OperationParameters parameters)
    {
        var payload = parameters.GetObject(PayloadKey);
        if (payload is null || payload.Count == 0)
        {
            throw OperationValidationException.Missing("任务内容（payload）");
        }

        return (JsonObject)payload.DeepClone();
    }

    /// <summary>创建：校验并补齐默认值；返回可提交给 1Panel 的完整载荷。</summary>
    public static JsonObject NormalizeCreate(OperationParameters parameters)
    {
        var payload = Require(parameters);

        var name = RequireString(payload, "name", "任务名称");
        var type = RequireString(payload, "type", "任务类型");
        if (!CronjobTypes.IsKnown(type))
        {
            throw new OperationValidationException($"未知任务类型：{type}。");
        }

        if (!CronjobTypes.IsCreateSupported(type))
        {
            throw new OperationValidationException($"创建表单暂不支持类型「{CronjobTypes.Label(type)}」，请在 1Panel 中创建。");
        }

        var spec = RequireString(payload, "spec", "周期表达式");
        NormalizeCommon(payload, name, spec);
        payload["type"] = type;

        ValidateTypeFields(payload, type, isCreate: true);
        payload.Remove("secret");
        return payload;
    }

    /// <summary>更新：校验 name/spec 与数值；type 由调用方保留（不信任前端提交的 type）。</summary>
    public static JsonObject NormalizeUpdate(OperationParameters parameters)
    {
        var payload = Require(parameters);
        var name = RequireString(payload, "name", "任务名称");
        var spec = RequireString(payload, "spec", "周期表达式");
        NormalizeCommon(payload, name, spec);
        payload.Remove("type");
        payload.Remove("secret");
        return payload;
    }

    /// <summary>把前端提交的字段合并回 1Panel 当前载荷（保留未展示字段与 secret）。</summary>
    public static JsonObject Merge(JsonElement current, JsonObject payload)
    {
        var merged = JsonNode.Parse(current.GetRawText()) as JsonObject ?? new JsonObject();
        foreach (var pair in payload)
        {
            merged[pair.Key] = pair.Value?.DeepClone();
        }

        return merged;
    }

    private static void NormalizeCommon(JsonObject payload, string name, string spec)
    {
        if (name.Length > 128 || !NamePattern().IsMatch(name))
        {
            throw new OperationValidationException("任务名称只能包含字母、数字、下划线、点和短横线，且不超过 128 字符。");
        }

        if (!CronjobSpec.IsValid(spec))
        {
            throw new OperationValidationException($"周期表达式不合法：{spec}。");
        }

        payload["name"] = name;
        payload["spec"] = Trim(spec);
        payload["groupID"] = Math.Max(0, GetLong(payload, "groupID"));
        payload["specCustom"] = GetBool(payload, "specCustom");
        payload["retainCopies"] = ClampNumber(payload, "retainCopies", 1, 365, 7);
        payload["retryTimes"] = ClampNumber(payload, "retryTimes", 0, 10, 0);
        payload["timeout"] = ClampNumber(payload, "timeout", 1, 604800, 3600);
        payload["ignoreErr"] = GetBool(payload, "ignoreErr");
        payload["args"] = Trim(payload["args"]);
    }

    private static void ValidateTypeFields(JsonObject payload, string type, bool isCreate)
    {
        switch (type)
        {
            case CronjobTypes.Shell:
            {
                var scriptMode = GetString(payload, "scriptMode") ?? "input";
                if (scriptMode is not ("input" or "library" or "select"))
                {
                    throw new OperationValidationException("Shell 脚本模式不合法。");
                }

                payload["scriptMode"] = scriptMode;
                var executor = Trim(GetString(payload, "executor"));
                if (isCreate && string.IsNullOrEmpty(executor))
                {
                    executor = "bash";
                }

                if (!string.IsNullOrEmpty(executor) && executor is not ("bash" or "sh" or "python" or "python3"))
                {
                    throw new OperationValidationException("执行器只支持 bash/sh/python/python3。");
                }

                payload["executor"] = executor;
                payload["command"] = Trim(GetString(payload, "command"));
                payload["containerName"] = Trim(GetString(payload, "containerName"));
                payload["user"] = Trim(GetString(payload, "user"));

                if (scriptMode == "library")
                {
                    if (GetLong(payload, "scriptID") <= 0)
                    {
                        throw new OperationValidationException("脚本库模式必须选择脚本（scriptID）。");
                    }
                }
                else
                {
                    var script = GetString(payload, "script") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(script))
                    {
                        throw new OperationValidationException("Shell 脚本内容不能为空。");
                    }

                    if (script.Length > 262144)
                    {
                        throw new OperationValidationException("Shell 脚本内容超过 256 KiB 上限。");
                    }

                    payload["script"] = script;
                }

                break;
            }

            case CronjobTypes.Curl:
            {
                var url = Trim(GetString(payload, "url"));
                if (string.IsNullOrEmpty(url))
                {
                    throw new OperationValidationException("URL 访问任务必须填写 URL。");
                }

                foreach (var item in url.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Uri.TryCreate(item, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        throw new OperationValidationException($"URL 不合法：{item}。");
                    }
                }

                payload["url"] = url;
                break;
            }

            case CronjobTypes.Directory:
            {
                var sourceDir = Trim(GetString(payload, "sourceDir"));
                if (string.IsNullOrEmpty(sourceDir))
                {
                    throw new OperationValidationException("备份目录任务必须填写源目录。");
                }

                if (sourceDir.Length > 4096 || sourceDir.Any(char.IsControl))
                {
                    throw new OperationValidationException("源目录不合法。");
                }

                payload["sourceDir"] = sourceDir;
                payload["isDir"] = payload.ContainsKey("isDir") ? GetBool(payload, "isDir") : true;
                payload["exclusionRules"] = Trim(GetString(payload, "exclusionRules"));
                break;
            }

            case CronjobTypes.Website:
            {
                var website = Trim(GetString(payload, "website"));
                if (string.IsNullOrEmpty(website))
                {
                    throw new OperationValidationException("备份网站任务必须填写网站域名。");
                }

                if (website.Length > 1024 || website.Any(char.IsControl))
                {
                    throw new OperationValidationException("网站域名不合法。");
                }

                payload["website"] = website;
                break;
            }

            case CronjobTypes.App:
            {
                var appId = Trim(GetString(payload, "appID"));
                if (string.IsNullOrEmpty(appId))
                {
                    throw new OperationValidationException("备份应用任务必须填写应用 key（如 jenkins）。");
                }

                if (appId.Length > 128 || !AppKeyPattern().IsMatch(appId))
                {
                    throw new OperationValidationException("应用 key 不合法。");
                }

                payload["appID"] = appId;
                break;
            }

            case CronjobTypes.Database:
            {
                var dbType = Trim(GetString(payload, "dbType"));
                if (!DatabaseTypes.Contains(dbType, StringComparer.OrdinalIgnoreCase))
                {
                    throw new OperationValidationException("数据库类型不合法（mysql/mariadb/mysql-cluster/postgresql/postgresql-cluster/mongodb）。");
                }

                var dbName = Trim(GetString(payload, "dbName"));
                if (string.IsNullOrEmpty(dbName) || !long.TryParse(dbName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dbId) || dbId <= 0)
                {
                    throw new OperationValidationException("数据库任务必须填写有效的数据库记录 ID。");
                }

                payload["dbType"] = dbType;
                payload["dbName"] = dbName;
                break;
            }

            default:
                throw new OperationValidationException($"创建表单暂不支持类型「{CronjobTypes.Label(type)}」。");
        }
    }

    private static string RequireString(JsonObject payload, string name, string label)
    {
        var value = Trim(GetString(payload, name));
        if (string.IsNullOrEmpty(value))
        {
            throw OperationValidationException.Missing(label);
        }

        return value;
    }

    private static string Trim(JsonNode? node) => (CronjobJson.ToStringValue(node) ?? string.Empty).Trim();

    private static string? GetString(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var node) ? CronjobJson.ToStringValue(node) : null;

    private static long GetLong(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var node) ? CronjobJson.ToLong(node) ?? 0 : 0;

    private static bool GetBool(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var node) && CronjobJson.ToBool(node);

    private static int ClampNumber(JsonObject payload, string name, int min, int max, int fallback)
    {
        var raw = GetLong(payload, name);
        var value = raw <= 0 && !payload.ContainsKey(name) ? fallback : raw;
        return (int)Math.Clamp(value, min, max);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppKeyPattern();
}

/// <summary>基于任务 id 的批量操作基类（删除/启停/立即执行）。</summary>
public abstract class CronjobTargetHandlerBase : IOperationHandler
{
    protected CronjobTargetHandlerBase(CronjobOptions options) => Options = options;

    public abstract string Kind { get; }

    protected abstract string Verb { get; }

    protected abstract string Title { get; }

    protected abstract OperationRisk SingleTargetRisk { get; }

    protected virtual bool IsDestructive => false;

    protected virtual string? TypedConfirmationText(OperationPlanContext context, int nodes, int total) => null;

    protected CronjobOptions Options { get; }

    public virtual Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var map = CronjobTargets.Parse(context.Parameters, context.Nodes, Options.MaxBatchJobs);
        var total = map.Values.Sum(ids => ids.Count);
        var isBatch = context.Nodes.Count > 1 || total > 1;
        var risk = isBatch ? OperationRisk.Critical : SingleTargetRisk;

        var warnings = new List<string>
        {
            "写操作不会自动回滚；执行结果以 1Panel 实际状态为准。"
        };
        if (isBatch)
        {
            warnings.Add("批量操作按节点顺序执行，某节点失败后停止后续节点与剩余任务；已执行的操作不回滚。");
        }

        if (IsDestructive)
        {
            warnings.Add("删除计划任务不可恢复；勾选清理备份数据时会同时删除 1Panel 备份记录。");
        }

        AppendWarnings(warnings, context);

        var typedConfirmation = TypedConfirmationText(context, context.Nodes.Count, total);
        var summary = isBatch
            ? $"在 {context.Nodes.Count} 个节点上{Verb} {total} 个计划任务（按节点顺序执行）。"
            : $"在节点 {context.Nodes[0].Name} 上{Verb} {total} 个计划任务。";

        return Task.FromResult(new OperationPlan(
            Title,
            summary,
            risk,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            risk == OperationRisk.Critical ? typedConfirmation ?? $"确认{Verb}{context.Nodes.Count}个节点共{total}个计划任务" : null,
            "cronjob"));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var map = CronjobTargets.Parse(
            context.Parameters,
            [context.Node],
            Options.MaxBatchJobs,
            ignoreUnknownNodes: true);
        var ids = map[context.Node.Id.Value];
        if (ids.Count == 0)
        {
            return Failure(context.Node, "没有可操作的计划任务。", null, null);
        }

        var applied = new List<long>();
        var started = DateTimeOffset.UtcNow;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = await ExecuteOneAsync(context, id, cancellationToken).ConfigureAwait(false);
            if (!step.Success)
            {
                return step with
                {
                    Message = applied.Count == 0
                        ? step.Message
                        : $"已成功处理 {applied.Count} 个任务后失败：{step.Message}",
                    DetailJson = BuildDetail(ids, applied)
                };
            }

            applied.Add(id);
        }

        var message = ids.Count == 1
            ? $"已{Verb}计划任务 #{ids[0]}。"
            : $"已{Verb} {ids.Count} 个计划任务。";
        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            message,
            null,
            BuildDetail(ids, applied),
            null,
            DateTimeOffset.UtcNow - started,
            ids.Count == 1 ? ids[0].ToString(CultureInfo.InvariantCulture) : "batch");
    }

    protected abstract Task<OperationStepResult> ExecuteOneAsync(
        OperationNodeContext context,
        long id,
        CancellationToken cancellationToken);

    protected virtual void AppendWarnings(List<string> warnings, OperationPlanContext context)
    {
    }

    protected static string? BuildDetail(IReadOnlyList<long> ids, IReadOnlyList<long> applied) =>
        new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(id => (JsonNode?)id).ToArray()),
            ["applied"] = new JsonArray(applied.Select(id => (JsonNode?)id).ToArray())
        }.ToJsonString();

    protected OperationStepResult Failure(NodeRecord node, string message, PanelError? error, string? detail) =>
        new(node.Id, node.Name, false, message, null, detail, error, TimeSpan.Zero, null);
}

/// <summary>基于表单载荷的操作基类（创建/编辑，仅单节点单任务）。</summary>
public abstract class CronjobFormHandlerBase : IOperationHandler
{
    protected CronjobFormHandlerBase(CronjobOptions options) => Options = options;

    public abstract string Kind { get; }

    protected abstract string Verb { get; }

    protected abstract string Title { get; }

    protected CronjobOptions Options { get; }

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Nodes.Count != 1)
        {
            throw new OperationValidationException("创建/编辑计划任务只能选择一个节点。");
        }

        var payload = Normalize(context);
        var name = CronjobJson.ToStringValue(payload["name"]) ?? "(未命名)";
        var type = CronjobJson.ToStringValue(payload["type"]) ?? string.Empty;
        var spec = CronjobJson.ToStringValue(payload["spec"]) ?? string.Empty;

        var warnings = new List<string>
        {
            "写操作不会自动回滚；提交后请在 1Panel 或本页刷新确认。",
            "周期表达式将以 1Panel 节点时区解释。"
        };

        return Task.FromResult(new OperationPlan(
            Title,
            $"在节点 {context.Nodes[0].Name} 上{Verb}计划任务「{name}」（{CronjobTypes.Label(type)}，{CronjobSpec.Describe(spec)}）。",
            OperationRisk.Medium,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            null,
            "cronjob"));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = DateTimeOffset.UtcNow;

        try
        {
            return await ExecuteCoreAsync(context, started, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationValidationException exception)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                exception.Message,
                null,
                null,
                new PanelError(PanelErrorKind.Validation, exception.Message),
                DateTimeOffset.UtcNow - started,
                null);
        }
    }

    protected abstract JsonObject Normalize(OperationPlanContext context);

    protected abstract Task<OperationStepResult> ExecuteCoreAsync(
        OperationNodeContext context,
        DateTimeOffset started,
        CancellationToken cancellationToken);

    protected static OperationStepResult Success(
        OperationNodeContext context,
        DateTimeOffset started,
        string message,
        string targetId,
        JsonObject detail) =>
        new(
            context.Node.Id,
            context.Node.Name,
            true,
            message,
            null,
            detail.ToJsonString(),
            null,
            DateTimeOffset.UtcNow - started,
            targetId);

    protected static OperationStepResult Failure(
        OperationNodeContext context,
        DateTimeOffset started,
        string message,
        PanelError? error,
        string? targetId = null) =>
        new(
            context.Node.Id,
            context.Node.Name,
            false,
            message,
            null,
            null,
            error,
            DateTimeOffset.UtcNow - started,
            targetId);
}

/// <summary>创建计划任务（cronjob.create，Medium；仅单节点）。</summary>
public sealed class CronjobCreateHandler : CronjobFormHandlerBase
{
    public CronjobCreateHandler(CronjobOptions options)
        : base(options)
    {
    }

    public override string Kind => "cronjob.create";

    protected override string Verb => "创建";

    protected override string Title => "创建计划任务";

    protected override JsonObject Normalize(OperationPlanContext context) => CronjobPayload.NormalizeCreate(context.Parameters);

    protected override async Task<OperationStepResult> ExecuteCoreAsync(
        OperationNodeContext context,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var payload = CronjobPayload.NormalizeCreate(context.Parameters);
        var result = await context.Client
            .PostAsync<JsonElement>(CronjobRoutes.Create, payload, cancellationToken)
            .ConfigureAwait(false);

        var name = CronjobJson.ToStringValue(payload["name"]) ?? string.Empty;
        if (result.Error is not null)
        {
            return Failure(context, started, "1Panel 拒绝创建：" + result.Error.Message, result.Error, name);
        }

        return Success(
            context,
            started,
            $"已创建计划任务「{name}」（{CronjobTypes.Label(CronjobJson.ToStringValue(payload["type"]))}），默认启用；请刷新列表确认。",
            name,
            new JsonObject
            {
                ["name"] = name,
                ["type"] = CronjobJson.ToStringValue(payload["type"]),
                ["groupID"] = CronjobJson.ToLong(payload["groupID"]) ?? 0
            });
    }
}

/// <summary>编辑计划任务（cronjob.update，Medium；仅单节点，保留未展示字段与 secret）。</summary>
public sealed class CronjobUpdateHandler : CronjobFormHandlerBase
{
    public CronjobUpdateHandler(CronjobOptions options)
        : base(options)
    {
    }

    public override string Kind => "cronjob.update";

    protected override string Verb => "更新";

    protected override string Title => "编辑计划任务";

    protected override JsonObject Normalize(OperationPlanContext context) => CronjobPayload.NormalizeUpdate(context.Parameters);

    protected override async Task<OperationStepResult> ExecuteCoreAsync(
        OperationNodeContext context,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var payload = CronjobPayload.NormalizeUpdate(context.Parameters);
        var id = context.Parameters.GetInt64("id") ?? CronjobJson.ToLong(payload["id"]) ?? 0;
        if (id <= 0)
        {
            return Failure(
                context,
                started,
                "缺少计划任务 id。",
                new PanelError(PanelErrorKind.Validation, "缺少计划任务 id。"));
        }

        var current = await context.Client
            .PostAsync<JsonElement>(CronjobRoutes.Info, new CronjobIdRequest(id), cancellationToken)
            .ConfigureAwait(false);
        if (current.Error is not null || current.Data.ValueKind != JsonValueKind.Object)
        {
            var error = current.Error ?? new PanelError(PanelErrorKind.NotFound, "计划任务不存在。");
            return Failure(context, started, "读取计划任务失败：" + error.Message, error, id.ToString(CultureInfo.InvariantCulture));
        }

        var merged = CronjobPayload.Merge(current.Data, payload);
        merged["id"] = id;

        var result = await context.Client
            .PostAsync<JsonElement>(CronjobRoutes.Update, merged, cancellationToken)
            .ConfigureAwait(false);

        var name = CronjobJson.ToStringValue(payload["name"]) ?? $"#{id}";
        if (result.Error is not null)
        {
            return Failure(context, started, "1Panel 拒绝更新：" + result.Error.Message, result.Error, name);
        }

        return Success(
            context,
            started,
            $"已更新计划任务「{name}」；请刷新列表确认。",
            name,
            new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["type"] = CronjobJson.ToStringValue(merged["type"])
            });
    }
}

/// <summary>删除计划任务（cronjob.delete，High；批量 Critical；不可回滚）。</summary>
public sealed class CronjobDeleteHandler : CronjobTargetHandlerBase
{
    public CronjobDeleteHandler(CronjobOptions options)
        : base(options)
    {
    }

    public override string Kind => "cronjob.delete";

    protected override string Verb => "删除";

    protected override string Title => "删除计划任务";

    protected override OperationRisk SingleTargetRisk => OperationRisk.High;

    protected override bool IsDestructive => true;

    protected override string? TypedConfirmationText(OperationPlanContext context, int nodes, int total) =>
        $"确认删除{nodes}个节点共{total}个计划任务";

    protected override void AppendWarnings(List<string> warnings, OperationPlanContext context)
    {
        if (context.Parameters.GetBoolean("cleanData"))
        {
            warnings.Add("已勾选「同时清理备份数据」：会删除该任务产生的 1Panel 备份记录，不可恢复。");
        }
    }

    protected override async Task<OperationStepResult> ExecuteOneAsync(
        OperationNodeContext context,
        long id,
        CancellationToken cancellationToken)
    {
        var cleanData = context.Parameters.GetBoolean("cleanData");
        var cleanRemoteData = context.Parameters.GetBoolean("cleanRemoteData");
        var result = await context.Client
            .PostAsync<JsonElement>(
                CronjobRoutes.Delete,
                new CronjobDeleteRequest([id], cleanData, cleanRemoteData),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Error is not null
            ? new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                $"删除计划任务 #{id} 失败：{result.Error.Message}",
                null,
                null,
                result.Error,
                result.Elapsed,
                id.ToString(CultureInfo.InvariantCulture))
            : new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                true,
                $"已删除计划任务 #{id}。",
                null,
                null,
                null,
                result.Elapsed,
                id.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>启用计划任务（cronjob.enable，Medium；批量 Critical）。</summary>
public sealed class CronjobEnableHandler : CronjobTargetHandlerBase
{
    public CronjobEnableHandler(CronjobOptions options)
        : base(options)
    {
    }

    public override string Kind => "cronjob.enable";

    protected override string Verb => "启用";

    protected override string Title => "启用计划任务";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override Task<OperationStepResult> ExecuteOneAsync(
        OperationNodeContext context,
        long id,
        CancellationToken cancellationToken) =>
        CronjobStatusOperations.ChangeStatusAsync(
            context,
            id,
            CronjobStatus.Enable,
            "启用",
            cancellationToken);
}

/// <summary>停用计划任务（cronjob.disable，Medium；批量 Critical）。</summary>
public sealed class CronjobDisableHandler : CronjobTargetHandlerBase
{
    public CronjobDisableHandler(CronjobOptions options)
        : base(options)
    {
    }

    public override string Kind => "cronjob.disable";

    protected override string Verb => "停用";

    protected override string Title => "停用计划任务";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override Task<OperationStepResult> ExecuteOneAsync(
        OperationNodeContext context,
        long id,
        CancellationToken cancellationToken) =>
        CronjobStatusOperations.ChangeStatusAsync(
            context,
            id,
            CronjobStatus.Disable,
            "停用",
            cancellationToken);
}

/// <summary>立即执行计划任务（cronjob.run_now，Medium；批量 Critical）。</summary>
public sealed class CronjobRunNowHandler : CronjobTargetHandlerBase
{
    public CronjobRunNowHandler(CronjobOptions options)
        : base(options)
    {
    }

    public override string Kind => "cronjob.run_now";

    protected override string Verb => "立即执行";

    protected override string Title => "立即执行计划任务";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override void AppendWarnings(List<string> warnings, OperationPlanContext context)
    {
        warnings.Add("立即执行会真实运行任务（可能产生备份/清理/系统变更），执行记录可在任务详情中查看。");
    }

    protected override async Task<OperationStepResult> ExecuteOneAsync(
        OperationNodeContext context,
        long id,
        CancellationToken cancellationToken)
    {
        var result = await context.Client
            .PostAsync<JsonElement>(CronjobRoutes.Handle, new CronjobIdRequest(id), cancellationToken)
            .ConfigureAwait(false);

        return result.Error is not null
            ? new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                $"执行计划任务 #{id} 失败：{result.Error.Message}",
                null,
                null,
                result.Error,
                result.Elapsed,
                id.ToString(CultureInfo.InvariantCulture))
            : new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                true,
                $"已提交立即执行计划任务 #{id}，结果请在执行记录中查看。",
                null,
                null,
                null,
                result.Elapsed,
                id.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>启停共用的状态提交逻辑。</summary>
internal static class CronjobStatusOperations
{
    public static async Task<OperationStepResult> ChangeStatusAsync(
        OperationNodeContext context,
        long id,
        string status,
        string verb,
        CancellationToken cancellationToken)
    {
        var result = await context.Client
            .PostAsync<JsonElement>(CronjobRoutes.Status, new CronjobStatusRequest(id, status), cancellationToken)
            .ConfigureAwait(false);

        return result.Error is not null
            ? new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                $"{verb}计划任务 #{id} 失败：{result.Error.Message}",
                null,
                null,
                result.Error,
                result.Elapsed,
                id.ToString(CultureInfo.InvariantCulture))
            : new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                true,
                $"已{verb}计划任务 #{id}。",
                null,
                null,
                null,
                result.Elapsed,
                id.ToString(CultureInfo.InvariantCulture));
    }
}
