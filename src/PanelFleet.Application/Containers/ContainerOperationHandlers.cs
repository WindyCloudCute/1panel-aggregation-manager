using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Containers;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Containers;

/// <summary>容器操作参数（<c>container</c> 单容器 / <c>containersByNode</c> 批量）：解析与校验。</summary>
public static class ContainerTargets
{
    public const string SingleKey = "container";

    public const string ByNodeKey = "containersByNode";

    public const string SourceKey = "source";

    private static readonly Regex ContainerNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>校验容器名：符合 Docker 命名规则（1-128 位字母数字与 _ . -）。</summary>
    public static bool IsValidContainerName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ContainerNamePattern.IsMatch(name.Trim());

    /// <summary>
    /// 解析节点 → 容器名列表。
    /// <para>优先使用 <c>containersByNode</c>（批量跨节点，值为节点 id → 名称数组）；否则使用 <c>container</c> 应用到所有节点。</para>
    /// </summary>
    /// <param name="ignoreUnknownNodes">
    /// 执行阶段内核只传入当前节点，但批量参数包含其它节点的键：为 true 时忽略不属于当前节点的键。
    /// </param>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<string>> Parse(
        OperationParameters parameters,
        IReadOnlyList<NodeRecord> nodes,
        bool ignoreUnknownNodes = false)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Count == 0)
        {
            throw new OperationValidationException("至少选择一个节点。");
        }

        var nodeIds = nodes.Select(node => node.Id.Value).ToHashSet();
        var map = new Dictionary<Guid, IReadOnlyList<string>>();

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

                var names = ReadNames(pair.Value);
                if (names.Count == 0)
                {
                    throw new OperationValidationException($"节点 {pair.Key} 未提供有效的容器名称。");
                }

                map[nodeId] = names;
            }

            foreach (var node in nodes)
            {
                if (!map.ContainsKey(node.Id.Value))
                {
                    throw new OperationValidationException($"节点 {node.Name} 未提供容器列表。");
                }
            }

            return map;
        }

        var single = parameters.GetString(SingleKey);
        if (string.IsNullOrWhiteSpace(single))
        {
            throw OperationValidationException.Missing("容器名称（container 或 containersByNode）");
        }

        var singleNames = ReadNames(JsonValue.Create(single.Trim()));
        if (singleNames.Count == 0)
        {
            throw new OperationValidationException("容器名称不合法。");
        }

        foreach (var node in nodes)
        {
            map[node.Id.Value] = singleNames;
        }

        return map;
    }

    private static IReadOnlyList<string> ReadNames(JsonNode? node)
    {
        var names = new List<string>();
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    AddName(names, Stringify(item));
                }

                break;
            case JsonValue value:
                AddName(names, Stringify(value));
                break;
        }

        return names;

        static string? Stringify(JsonNode? item) =>
            item is JsonValue value && value.TryGetValue<string>(out var text) ? text : item?.ToJsonString().Trim('"');
    }

    private static void AddName(List<string> names, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var name = raw.Trim();
        if (!IsValidContainerName(name))
        {
            throw new OperationValidationException($"容器名称不合法：{TruncateName(name)}。");
        }

        if (!names.Contains(name, StringComparer.Ordinal))
        {
            names.Add(name);
        }
    }

    private static string TruncateName(string value) => value.Length <= 64 ? value : value[..64] + "…";
}

/// <summary>
/// 容器写操作处理器基类：解析目标 → 预览（风险/警告/强确认文本）→ 提交 1Panel 异步操作并尽力确认状态。
/// </summary>
public abstract class ContainerOperationHandlerBase : IOperationHandler
{
    private static readonly TimeSpan VerifyInterval = TimeSpan.FromMilliseconds(250);

    protected ContainerOperationHandlerBase(ContainerOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public abstract string Kind { get; }

    /// <summary>1Panel ContainerOperation.operation 值：start/stop/restart/pause/unpause/remove。</summary>
    protected abstract string Operation { get; }

    protected abstract string Verb { get; }

    protected abstract string Title { get; }

    protected abstract OperationRisk SingleTargetRisk { get; }

    /// <summary>提交后预期的容器状态；null 表示不做状态确认（如删除用不存在确认）。</summary>
    protected virtual string? ExpectedState => null;

    /// <summary>是否按删除语义确认（inspect 不存在即成功）。</summary>
    protected virtual bool IsRemoval => false;

    protected ContainerOptions Options { get; }

    public virtual Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var map = ContainerTargets.Parse(context.Parameters, context.Nodes);
        var total = map.Values.Sum(names => names.Count);

        if (total > Options.MaxBatchContainers)
        {
            throw new OperationValidationException($"单次最多操作 {Options.MaxBatchContainers} 个容器。");
        }

        var isBatch = context.Nodes.Count > 1 || total > 1;
        var risk = isBatch ? OperationRisk.Critical : SingleTargetRisk;
        var warnings = new List<string>
        {
            "写操作不会自动回滚；执行结果以容器实际状态为准。"
        };

        if (isBatch)
        {
            warnings.Add("批量操作按节点顺序执行，某节点失败后停止后续节点；已执行的操作不回滚。");
        }

        AppendWarnings(warnings, context.Parameters, map);
        if (risk >= OperationRisk.High)
        {
            warnings.Add("该操作风险较高，请再次核对目标节点与容器名称。");
        }

        var targets = context.Nodes
            .Select(node => new OperationTarget(node.Id, node.Name))
            .ToArray();
        var summary = BuildSummary(context.Nodes, map, total);
        var typedConfirmation = risk == OperationRisk.Critical
            ? $"确认{Verb}{context.Nodes.Count}个节点共{total}个容器"
            : null;

        return Task.FromResult(new OperationPlan(
            Title,
            summary,
            risk,
            targets,
            warnings,
            typedConfirmation,
            "container"));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var map = ContainerTargets.Parse(context.Parameters, [context.Node], ignoreUnknownNodes: true);
        var names = map[context.Node.Id.Value];
        if (names.Count == 0)
        {
            return Failure(context.Node, "没有可操作的容器。", null, TimeSpan.Zero);
        }

        var taskId = Guid.NewGuid().ToString("N");
        var submit = await context.Client
            .PostAsync<JsonElement>(
                ContainerRoutes.Operate,
                new ContainerOperateRequest(taskId, names, Operation),
                cancellationToken)
            .ConfigureAwait(false);

        if (submit.Error is not null)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                "1Panel 拒绝执行：" + submit.Error.Message,
                taskId,
                null,
                submit.Error,
                TimeSpan.Zero,
                names[0]);
        }

        var verifyMessage = await VerifyAsync(context, names, cancellationToken).ConfigureAwait(false);
        var message = verifyMessage
            ?? $"已提交 1Panel 异步任务，未在 {Options.VerifyTimeoutSeconds} 秒内确认状态（请稍后刷新列表）。";

        var detail = new JsonObject
        {
            ["operation"] = Operation,
            ["containers"] = new JsonArray(names.Select(name => (JsonNode?)name).ToArray()),
            ["verified"] = verifyMessage is not null
        };

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            message,
            taskId,
            detail.ToJsonString(),
            null,
            TimeSpan.Zero,
            names[0]);
    }

    protected virtual void AppendWarnings(
        List<string> warnings,
        OperationParameters parameters,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> targets)
    {
        var source = parameters.GetString(ContainerTargets.SourceKey);
        if (string.Equals(source, "Compose", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("目标容器由 Compose 管理：单独操作容器可能与编排期望状态不一致。");
        }
        else if (string.Equals(source, "应用", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("目标容器由 1Panel 应用管理：单独操作可能影响应用整体状态。");
        }
    }

    private async Task<string?> VerifyAsync(
        OperationNodeContext context,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        if (Options.VerifyTimeoutSeconds <= 0)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow.Add(Options.VerifyTimeout);
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRemoval)
                {
                    var allGone = true;
                    foreach (var name in names)
                    {
                        var inspect = await InspectStateAsync(context.Client, name, cancellationToken).ConfigureAwait(false);
                        if (inspect.Present)
                        {
                            allGone = false;
                            break;
                        }
                    }

                    if (allGone)
                    {
                        return names.Count == 1
                            ? "已确认容器已删除。"
                            : $"已确认 {names.Count} 个容器已删除。";
                    }
                }
                else if (ExpectedState is { } expected)
                {
                    var allMatch = true;
                    foreach (var name in names)
                    {
                        var inspect = await InspectStateAsync(context.Client, name, cancellationToken).ConfigureAwait(false);
                        if (!inspect.Present
                            || !string.Equals(
                                ContainerStates.Normalize(inspect.State),
                                expected,
                                StringComparison.Ordinal))
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch)
                    {
                        return names.Count == 1
                            ? $"已确认容器状态：{expected}。"
                            : $"已确认 {names.Count} 个容器状态：{expected}。";
                    }
                }
                else
                {
                    return null;
                }

                await Task.Delay(VerifyInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 确认预算被动取消（节点超时/用户取消）时返回 null，提交本身已成功。
        }

        return null;
    }

    private static async Task<(bool Present, string? State)> InspectStateAsync(
        IPanelClient client,
        string name,
        CancellationToken cancellationToken)
    {
        var inspect = await client
            .PostAsync<JsonElement>(
                ContainerRoutes.Inspect,
                new ContainerInspectRequest(name, "container"),
                cancellationToken)
            .ConfigureAwait(false);

        if (inspect.Error is not null)
        {
            return (false, null);
        }

        var raw = inspect.Data.ValueKind == JsonValueKind.String ? inspect.Data.GetString() : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (false, null);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("State", out var state)
                && state.TryGetProperty("Status", out var status)
                ? (true, status.GetString())
                : (true, null);
        }
        catch (JsonException)
        {
            return (true, null);
        }
    }

    private string BuildSummary(
        IReadOnlyList<NodeRecord> nodes,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> targets,
        int total)
    {
        if (nodes.Count == 1)
        {
            var names = targets[nodes[0].Id.Value];
            return $"在节点 {nodes[0].Name} 上{Verb}容器 {string.Join("、", names.Take(5))}"
                + (names.Count > 5 ? $" 等 {names.Count} 个" : string.Empty) + "。";
        }

        return $"在 {nodes.Count} 个节点上共{Verb} {total} 个容器（按节点顺序执行）。";
    }

    private static OperationStepResult Failure(
        NodeRecord node,
        string message,
        string? taskId,
        TimeSpan duration) =>
        new(node.Id, node.Name, false, message, taskId, null, null, duration);

    protected static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>容器启动（container.start，Medium；批量 Critical）。</summary>
public sealed class ContainerStartHandler : ContainerOperationHandlerBase
{
    public ContainerStartHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "container.start";

    protected override string Operation => "start";

    protected override string Verb => "启动";

    protected override string Title => "容器启动";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override string ExpectedState => ContainerStates.Running;
}

/// <summary>容器停止（container.stop，Medium；批量 Critical）。</summary>
public sealed class ContainerStopHandler : ContainerOperationHandlerBase
{
    public ContainerStopHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "container.stop";

    protected override string Operation => "stop";

    protected override string Verb => "停止";

    protected override string Title => "容器停止";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override string ExpectedState => ContainerStates.Exited;
}

/// <summary>容器重启（container.restart，Medium；批量 Critical）。</summary>
public sealed class ContainerRestartHandler : ContainerOperationHandlerBase
{
    public ContainerRestartHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "container.restart";

    protected override string Operation => "restart";

    protected override string Verb => "重启";

    protected override string Title => "容器重启";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override string ExpectedState => ContainerStates.Running;
}

/// <summary>容器暂停（container.pause，Medium；批量 Critical）。</summary>
public sealed class ContainerPauseHandler : ContainerOperationHandlerBase
{
    public ContainerPauseHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "container.pause";

    protected override string Operation => "pause";

    protected override string Verb => "暂停";

    protected override string Title => "容器暂停";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override string ExpectedState => ContainerStates.Paused;
}

/// <summary>容器恢复（container.unpause，Medium；批量 Critical）。</summary>
public sealed class ContainerUnpauseHandler : ContainerOperationHandlerBase
{
    public ContainerUnpauseHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "container.unpause";

    protected override string Operation => "unpause";

    protected override string Verb => "恢复";

    protected override string Title => "容器恢复";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;

    protected override string ExpectedState => ContainerStates.Running;
}

/// <summary>容器删除（container.delete，High；批量 Critical）。</summary>
public sealed class ContainerDeleteHandler : ContainerOperationHandlerBase
{
    public ContainerDeleteHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "container.delete";

    protected override string Operation => "remove";

    protected override string Verb => "删除";

    protected override string Title => "删除容器";

    protected override OperationRisk SingleTargetRisk => OperationRisk.High;

    protected override bool IsRemoval => true;

    protected override void AppendWarnings(
        List<string> warnings,
        OperationParameters parameters,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> targets)
    {
        base.AppendWarnings(warnings, parameters, targets);
        warnings.Add("删除容器会同时移除其匿名卷（1Panel 使用 Force + RemoveVolumes），数据不可恢复。");
        warnings.Add("1Panel 容器操作是异步任务；确认结果显示的是提交结果与尽力确认的容器状态。");
    }
}

/// <summary>容器重命名（container.rename，High，同步操作）。</summary>
public sealed class ContainerRenameHandler : IOperationHandler
{
    public ContainerRenameHandler()
    {
    }

    public string Kind => "container.rename";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = context.Parameters.RequireString("container", "容器名称");
        var newName = context.Parameters.RequireString("newName", "新容器名称");

        if (!ContainerTargets.IsValidContainerName(name))
        {
            throw new OperationValidationException($"容器名称不合法：{name}。");
        }

        if (!ContainerTargets.IsValidContainerName(newName))
        {
            throw new OperationValidationException($"新容器名称不合法：{newName}。");
        }

        if (string.Equals(name, newName, StringComparison.Ordinal))
        {
            throw new OperationValidationException("新容器名称与原名称相同。");
        }

        if (context.Nodes.Count > 1)
        {
            throw new OperationValidationException("容器重命名不支持批量跨节点执行，请逐节点操作。");
        }

        var warnings = new List<string>
        {
            "重命名会改变容器标识；依赖容器名的网站/应用/脚本可能失效。",
            "Compose 管理的容器重命名后可能无法被编排识别。"
        };

        return Task.FromResult(new OperationPlan(
            "容器重命名",
            $"在节点 {context.Nodes[0].Name} 上把容器 {name} 重命名为 {newName}。",
            OperationRisk.High,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            null,
            "container"));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = context.Parameters.RequireString("container", "容器名称");
        var newName = context.Parameters.RequireString("newName", "新容器名称");

        var result = await context.Client
            .PostAsync<JsonElement>(
                ContainerRoutes.Rename,
                new ContainerRenameRequest(name, newName),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                "1Panel 拒绝重命名：" + result.Error.Message,
                null,
                null,
                result.Error,
                result.Elapsed,
                name);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            $"容器已重命名为 {newName}。",
            null,
            null,
            null,
            result.Elapsed,
            newName);
    }
}

/// <summary>Compose 写操作处理器基类：参数 <c>compose</c>（项目名）与 <c>path</c>（Compose 文件路径）。</summary>
public abstract class ComposeOperationHandlerBase : IOperationHandler
{
    protected ComposeOperationHandlerBase(ContainerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public abstract string Kind { get; }

    /// <summary>1Panel ComposeOperation.operation：up/start/restart/stop/down/delete/rebuild。</summary>
    protected abstract string Operation { get; }

    protected abstract string Verb { get; }

    protected abstract string Title { get; }

    protected abstract OperationRisk SingleTargetRisk { get; }

    protected virtual bool IsDestructive => false;

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = context.Parameters.RequireString("compose", "Compose 项目名");
        var path = context.Parameters.RequireString("path", "Compose 文件路径");
        ValidateCompose(name, path);

        var isBatch = context.Nodes.Count > 1;
        var risk = isBatch ? OperationRisk.Critical : SingleTargetRisk;
        var warnings = new List<string>
        {
            "Compose 操作不会自动回滚；执行结果以项目容器实际状态为准。"
        };

        if (isBatch)
        {
            warnings.Add("批量操作按节点顺序执行，某节点失败后停止后续节点。");
        }

        if (IsDestructive)
        {
            warnings.Add("该操作会停止并移除该 Compose 项目的容器与网络，数据卷按 1Panel 语义处理，请确认。");
        }

        var typedConfirmation = risk == OperationRisk.Critical
            ? $"确认{Verb}{context.Nodes.Count}个节点的Compose项目"
            : null;

        return Task.FromResult(new OperationPlan(
            Title,
            isBatch
                ? $"在 {context.Nodes.Count} 个节点上{Verb} Compose 项目 {name}（按节点顺序执行）。"
                : $"在节点 {context.Nodes[0].Name} 上{Verb} Compose 项目 {name}。",
            risk,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            typedConfirmation,
            "compose"));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = context.Parameters.RequireString("compose", "Compose 项目名");
        var path = context.Parameters.RequireString("path", "Compose 文件路径");

        var result = await context.Client
            .PostAsync<JsonElement>(
                ContainerRoutes.ComposeOperate,
                new ComposeOperateRequest(name, path, Operation),
                cancellationToken)
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
                name);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            $"Compose 项目 {name} 已{Verb}（1Panel 同步返回成功）。",
            null,
            null,
            null,
            result.Elapsed,
            name);
    }

    protected virtual void ValidateCompose(string name, string path)
    {
        if (!ContainerTargets.IsValidContainerName(name))
        {
            throw new OperationValidationException($"Compose 项目名不合法：{name}。");
        }

        if (path.Length > 4096 || path.Any(char.IsControl))
        {
            throw new OperationValidationException("Compose 文件路径不合法。");
        }

        // 1Panel 服务端还会做命令注入字符检查（CheckIllegal）；这里提前拒绝明显危险字符。
        if (path.Contains("&&", StringComparison.Ordinal)
            || path.Contains(';')
            || path.Contains('|')
            || path.Contains('`')
            || path.Contains("$(", StringComparison.Ordinal))
        {
            throw new OperationValidationException("Compose 文件路径包含不允许的字符。");
        }
    }
}

/// <summary>Compose 启动（compose.up，Medium；批量 Critical）。</summary>
public sealed class ComposeUpHandler : ComposeOperationHandlerBase
{
    public ComposeUpHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "compose.up";

    protected override string Operation => "up";

    protected override string Verb => "启动";

    protected override string Title => "Compose 启动";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;
}

/// <summary>Compose 停止（compose.stop，Medium；批量 Critical）。</summary>
public sealed class ComposeStopHandler : ComposeOperationHandlerBase
{
    public ComposeStopHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "compose.stop";

    protected override string Operation => "stop";

    protected override string Verb => "停止";

    protected override string Title => "Compose 停止";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;
}

/// <summary>Compose 重启（compose.restart，Medium；批量 Critical）。</summary>
public sealed class ComposeRestartHandler : ComposeOperationHandlerBase
{
    public ComposeRestartHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "compose.restart";

    protected override string Operation => "restart";

    protected override string Verb => "重启";

    protected override string Title => "Compose 重启";

    protected override OperationRisk SingleTargetRisk => OperationRisk.Medium;
}

/// <summary>Compose 移除（compose.down，High；批量 Critical）。</summary>
public sealed class ComposeDownHandler : ComposeOperationHandlerBase
{
    public ComposeDownHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "compose.down";

    protected override string Operation => "down";

    protected override string Verb => "移除";

    protected override string Title => "Compose 移除";

    protected override OperationRisk SingleTargetRisk => OperationRisk.High;

    protected override bool IsDestructive => true;
}

/// <summary>Compose 重建（compose.rebuild = down + up，High；批量 Critical）。参数 compose/path。</summary>
public sealed class ComposeRebuildHandler : ComposeOperationHandlerBase
{
    public ComposeRebuildHandler(ContainerOptions options)
        : base(options)
    {
    }

    public override string Kind => "compose.rebuild";

    protected override string Operation => "rebuild";

    protected override string Verb => "重建";

    protected override string Title => "Compose 重建";

    protected override OperationRisk SingleTargetRisk => OperationRisk.High;

    protected override bool IsDestructive => true;
}

/// <summary>
/// Compose 创建（compose.create，单节点 High；批量 Critical）。参数 compose/content/env/forcePull。
/// <para>1Panel 创建是异步任务：HTTP 成功只代表已受理，结果需刷新列表/任务日志确认。</para>
/// </summary>
public sealed class ComposeCreateHandler : IOperationHandler
{
    public string Kind => "compose.create";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ComposeRules.RequireCreateName(context.Parameters);
        var content = ComposeRules.RequireContent(context.Parameters);
        _ = ComposeRules.OptionalEnv(context.Parameters);
        _ = context.Parameters.GetBoolean("forcePull", false);

        var isBatch = context.Nodes.Count > 1;
        var risk = isBatch ? OperationRisk.Critical : OperationRisk.High;
        var warnings = new List<string>
        {
            "创建为异步任务：1Panel 受理后需要拉取镜像并启动容器，请稍后刷新 Compose 列表查看结果。",
            "创建失败时可能留下部分容器或目录，不会自动回滚，请检查后手动清理。"
        };
        if (isBatch)
        {
            warnings.Add("批量操作按节点顺序执行，某节点失败后停止后续节点。");
        }

        return Task.FromResult(new OperationPlan(
            "Compose 创建",
            isBatch
                ? $"在 {context.Nodes.Count} 个节点上创建 Compose 项目 {name}（按节点顺序执行）。"
                : $"在节点 {context.Nodes[0].Name} 上创建 Compose 项目 {name}。",
            risk,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            isBatch ? $"确认创建{context.Nodes.Count}个节点的Compose项目" : null,
            "compose"));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ComposeRules.RequireCreateName(context.Parameters);
        var content = ComposeRules.RequireContent(context.Parameters);
        var env = ComposeRules.OptionalEnv(context.Parameters);
        var forcePull = context.Parameters.GetBoolean("forcePull", false);
        var taskId = Guid.NewGuid().ToString("N");

        var result = await context.Client
            .PostAsync<JsonElement>(
                ContainerRoutes.ComposeCreate,
                new ComposeCreateRequest(taskId, name, name, "edit", content, null, env, forcePull),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                "1Panel 拒绝创建：" + result.Error.Message,
                taskId,
                null,
                result.Error,
                result.Elapsed,
                name);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            $"已提交创建 Compose 项目 {name}（1Panel 后台任务已受理，请稍后刷新列表查看结果）。",
            taskId,
            null,
            null,
            result.Elapsed,
            name);
    }
}

/// <summary>
/// Compose 更新（compose.update，单节点 High；批量 Critical，异步任务）。
/// 参数 compose/path/content/detailPath?/env?/forcePull?；path 为项目目录（或配置文件路径），detailPath 为 yml 实际路径。
/// </summary>
public sealed class ComposeUpdateHandler : IOperationHandler
{
    public string Kind => "compose.update";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ComposeRules.RequireExistingName(context.Parameters);
        var path = context.Parameters.RequireString("path", "Compose 文件路径");
        _ = ComposeRules.SplitComposePaths(path, context.Parameters.GetString("detailPath"));
        _ = ComposeRules.RequireContent(context.Parameters);
        _ = ComposeRules.OptionalEnv(context.Parameters);
        _ = context.Parameters.GetBoolean("forcePull", false);

        var isBatch = context.Nodes.Count > 1;
        var risk = isBatch ? OperationRisk.Critical : OperationRisk.High;
        var warnings = new List<string>
        {
            "更新为异步任务：1Panel 写入配置后重建容器，失败时会尝试用旧文件回滚，但不保证容器恢复运行。",
            "执行结果以项目容器实际状态为准，不会自动回滚。"
        };
        if (isBatch)
        {
            warnings.Add("批量操作按节点顺序执行，某节点失败后停止后续节点。");
        }

        return Task.FromResult(new OperationPlan(
            "Compose 更新",
            isBatch
                ? $"在 {context.Nodes.Count} 个节点上更新 Compose 项目 {name}（按节点顺序执行）。"
                : $"在节点 {context.Nodes[0].Name} 上更新 Compose 项目 {name}。",
            risk,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            isBatch ? $"确认更新{context.Nodes.Count}个节点的Compose项目" : null,
            "compose"));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ComposeRules.RequireExistingName(context.Parameters);
        var path = context.Parameters.RequireString("path", "Compose 文件路径");
        var (directory, detailPath) = ComposeRules.SplitComposePaths(path, context.Parameters.GetString("detailPath"));
        var content = ComposeRules.RequireContent(context.Parameters);
        var env = ComposeRules.OptionalEnv(context.Parameters);
        var forcePull = context.Parameters.GetBoolean("forcePull", false);
        var taskId = Guid.NewGuid().ToString("N");

        var result = await context.Client
            .PostAsync<JsonElement>(
                ContainerRoutes.ComposeUpdate,
                new ComposeUpdateRequest(taskId, name, directory, detailPath, content, env, forcePull),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                "1Panel 拒绝更新：" + result.Error.Message,
                taskId,
                null,
                result.Error,
                result.Elapsed,
                name);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            $"已提交更新 Compose 项目 {name}（1Panel 后台任务已受理，请稍后刷新列表查看结果）。",
            taskId,
            null,
            null,
            result.Elapsed,
            name);
    }
}

/// <summary>
/// Compose 删除（compose.delete，始终 Critical，需输入项目名确认）。参数 compose/path?/withFile?/force?。
/// <para>删除会移除项目容器；withFile=true 时 1Panel 同时删除项目目录，不可恢复。</para>
/// </summary>
public sealed class ComposeDeleteHandler : IOperationHandler
{
    public string Kind => "compose.delete";

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ComposeRules.RequireExistingName(context.Parameters);
        var path = context.Parameters.GetString("path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            _ = ComposeRules.NormalizeAbsolutePath(path, "Compose 文件路径");
        }

        var withFile = context.Parameters.GetBoolean("withFile", false);
        var warnings = new List<string>
        {
            "删除会停止并移除该项目的容器，操作不可撤销。",
            "该操作不会自动回滚；删除后如需恢复只能重新创建项目。"
        };
        if (withFile)
        {
            warnings.Add("已选择同时删除 Compose 文件：1Panel 会删除项目目录，其中的配置文件将不可恢复。");
        }

        if (context.Nodes.Count > 1)
        {
            warnings.Add("批量操作按节点顺序执行，某节点失败后停止后续节点。");
        }

        return Task.FromResult(new OperationPlan(
            "Compose 删除",
            context.Nodes.Count > 1
                ? $"在 {context.Nodes.Count} 个节点上删除 Compose 项目 {name}（按节点顺序执行）。"
                : $"在节点 {context.Nodes[0].Name} 上删除 Compose 项目 {name}。",
            OperationRisk.Critical,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            name,
            "compose"));
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ComposeRules.RequireExistingName(context.Parameters);
        var path = context.Parameters.GetString("path") ?? string.Empty;
        var withFile = context.Parameters.GetBoolean("withFile", false);
        var force = context.Parameters.GetBoolean("force", false);

        var result = await context.Client
            .PostAsync<JsonElement>(
                ContainerRoutes.ComposeOperate,
                new ComposeOperateRequest(name, path, "delete", withFile, force),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                "1Panel 拒绝删除：" + result.Error.Message,
                null,
                null,
                result.Error,
                result.Elapsed,
                name);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            withFile
                ? $"Compose 项目 {name} 已删除，并已请求删除项目目录（1Panel 同步返回成功）。"
                : $"Compose 项目 {name} 已删除（1Panel 同步返回成功）。",
            null,
            null,
            null,
            result.Elapsed,
            name);
    }
}

/// <summary>Compose 增改删共用的参数校验规则。</summary>
internal static class ComposeRules
{
    public const int MaxContentChars = 256 * 1024;

    private static readonly Regex CreateNamePattern =
        new("^[a-z0-9][a-z0-9_-]{0,255}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>新项目名必须符合 1Panel 的 Compose 命名规则（小写字母/数字开头，仅小写字母、数字、_、-）。</summary>
    public static string RequireCreateName(OperationParameters parameters)
    {
        var name = parameters.RequireString("compose", "Compose 项目名").Trim();
        if (!CreateNamePattern.IsMatch(name))
        {
            throw new OperationValidationException(
                "Compose 项目名必须以小写字母或数字开头，只能包含小写字母、数字、下划线和连字符（1-256 位）。");
        }

        return name;
    }

    public static string RequireExistingName(OperationParameters parameters)
    {
        var name = parameters.RequireString("compose", "Compose 项目名").Trim();
        if (!ContainerTargets.IsValidContainerName(name))
        {
            throw new OperationValidationException($"Compose 项目名不合法：{name}。");
        }

        return name;
    }

    public static string RequireContent(OperationParameters parameters)
    {
        var content = parameters.RequireString("content", "Compose 文件内容");
        if (content.Length == 0)
        {
            throw new OperationValidationException("Compose 文件内容不能为空。");
        }

        if (content.Length > MaxContentChars)
        {
            throw new OperationValidationException($"Compose 文件内容不能超过 {MaxContentChars / 1024} KiB。");
        }

        if (content.Contains('\0'))
        {
            throw new OperationValidationException("Compose 文件内容包含二进制字符。");
        }

        return content;
    }

    public static string? OptionalEnv(OperationParameters parameters)
    {
        var env = parameters.GetString("env");
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        if (env.Length > MaxContentChars)
        {
            throw new OperationValidationException($"Compose .env 内容不能超过 {MaxContentChars / 1024} KiB。");
        }

        if (env.Contains('\0'))
        {
            throw new OperationValidationException("Compose .env 内容包含二进制字符。");
        }

        return env;
    }

    /// <summary>规范化绝对路径：必须以 / 开头、拒绝 ..、折叠重复斜杠，拒绝控制字符与命令注入字符。</summary>
    public static string NormalizeAbsolutePath(string path, string label)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        if (trimmed.Length == 0 || trimmed.Length > 4096 || trimmed.Any(char.IsControl))
        {
            throw new OperationValidationException($"{label}不合法。");
        }

        if (!trimmed.StartsWith('/'))
        {
            throw new OperationValidationException($"{label}必须是绝对路径。");
        }

        if (trimmed.Contains("&&", StringComparison.Ordinal)
            || trimmed.Contains(';')
            || trimmed.Contains('|')
            || trimmed.Contains('`')
            || trimmed.Contains("$(", StringComparison.Ordinal))
        {
            throw new OperationValidationException($"{label}包含不允许的字符。");
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new OperationValidationException($"{label}包含不允许的路径片段。");
        }

        return "/" + string.Join('/', segments.Where(segment => segment != "."));
    }

    /// <summary>把 path（目录或 yml 文件）与 detailPath（可选 yml 文件）解析为项目目录与 yml 实际路径。</summary>
    public static (string Directory, string FilePath) SplitComposePaths(string path, string? detailPath)
    {
        var normalized = NormalizeAbsolutePath(path, "Compose 文件路径");
        string directory;
        string filePath;
        if (EndsWithYaml(normalized))
        {
            directory = DirectoryOf(normalized);
            filePath = normalized;
        }
        else
        {
            directory = normalized == "/" ? "/" : normalized.TrimEnd('/');
            filePath = (directory == "/" ? string.Empty : directory) + "/docker-compose.yml";
        }

        if (!string.IsNullOrWhiteSpace(detailPath))
        {
            var detail = NormalizeAbsolutePath(detailPath, "Compose 文件路径");
            if (!EndsWithYaml(detail))
            {
                throw new OperationValidationException("Compose 文件路径必须以 .yml 或 .yaml 结尾。");
            }

            if (!detail.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                throw new OperationValidationException("Compose 文件必须位于项目目录下。");
            }

            filePath = detail;
        }

        return (directory, filePath);
    }

    private static bool EndsWithYaml(string path) =>
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    private static string DirectoryOf(string filePath)
    {
        var index = filePath.LastIndexOf('/');
        return index <= 0 ? "/" : filePath[..index];
    }
}
