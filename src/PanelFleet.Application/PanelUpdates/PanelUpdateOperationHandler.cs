using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Updates;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.PanelUpdates;

/// <summary>
/// 1Panel 面板升级写操作处理器（<c>panel.update</c>）。
/// <para>风险等级 Critical；单节点确认文本为节点名，批量确认文本为「升级 N 个节点」（由内核生成）。</para>
/// <para>执行流程：预检 → 提交升级（面板异步执行并重启）→ 轮询健康检查 + 版本回读 → 逐节点结果。
/// 无自动重试、无自动回滚；失败即停止由写操作内核保证。</para>
/// </summary>
public sealed class PanelUpdateOperationHandler : IOperationHandler
{
    /// <summary>写内核中该操作的 Kind。</summary>
    public const string HandlerKind = "panel.update";

    private readonly IPanelUpdateService _updates;
    private readonly PanelUpdateOptions _options;
    private readonly ILogger<PanelUpdateOperationHandler> _logger;

    public PanelUpdateOperationHandler(
        IPanelUpdateService updates,
        PanelUpdateOptions options,
        ILogger<PanelUpdateOperationHandler> logger)
    {
        _updates = updates;
        _options = options;
        _logger = logger;
    }

    public string Kind => HandlerKind;

    public async Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        var nodes = context.Nodes;
        var precheck = await _updates
            .PrecheckAsync(nodes.Select(node => node.Id).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var blocked = precheck.Items.Where(item => !item.CanUpgrade).ToArray();
        if (blocked.Length > 0)
        {
            throw new OperationValidationException(
                "以下节点不满足升级条件，已取消预览：" + string.Join(
                    "；",
                    blocked.Select(item =>
                        $"{item.NodeName}（{string.Join("、", item.BlockingReasons)}）")) + "。请重新检查更新后只选择可升级节点。");
        }

        var requestedTarget = context.Parameters.GetString("targetVersion")?.Trim();
        var targets = precheck.Items
            .Select(item => string.IsNullOrWhiteSpace(requestedTarget) ? item.TargetVersion : requestedTarget)
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target!.Trim())
            .ToArray();

        if (requestedTarget is { Length: > 0 })
        {
            if (!PanelVersionComparer.TryParse(requestedTarget, out _))
            {
                throw new OperationValidationException($"目标版本格式不合法：{requestedTarget}。");
            }

            foreach (var item in precheck.Items)
            {
                if (item.CurrentVersion is { } current
                    && PanelVersionComparer.Compare(requestedTarget, current) <= 0)
                {
                    throw new OperationValidationException(
                        $"目标版本 {requestedTarget} 不高于节点 {item.NodeName} 的当前版本 {current}，无法升级。");
                }
            }
        }

        var targetText = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() switch
        {
            [] => "各节点最新可用版本",
            [var single] => single,
            var many => string.Join("、", many)
        };

        var warnings = new List<string>
        {
            "1Panel 升级无自动回滚：升级失败需人工登录节点处理，PanelFleet 不会恢复旧版本。",
            "升级过程会重启面板，节点面板界面与 API 将中断约数十秒到数分钟；请安排在维护窗口内执行。",
            "升级后 PanelFleet 会自动回读版本并重新探测 dashboard/base/os 做健康检查；超时未恢复将标记失败，需人工确认。",
            "面板升级程序要求安装目录可用空间 ≥ 500 MiB，请在升级前确认磁盘余量。"
        };

        if (nodes.Count > 1)
        {
            warnings.Add("批量升级按所选顺序逐节点执行；任一节点失败立即停止后续节点，已升级节点不会回滚。");
        }

        var title = nodes.Count > 1 ? "批量升级 1Panel 面板" : "升级 1Panel 面板";
        var summary = nodes.Count > 1
            ? $"依次升级 {nodes.Count} 个节点（{string.Join("、", nodes.Select(node => node.Name))}），目标版本：{targetText}。失败即停止，无自动回滚。"
            : $"将节点「{nodes[0].Name}」的 1Panel 从 {precheck.Items[0].CurrentVersion ?? "未知版本"} 升级到 {targetText}，升级后自动做健康检查。";

        return new OperationPlan(
            title,
            summary,
            OperationRisk.Critical,
            nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            TypedConfirmationText: nodes.Count > 1 ? $"升级 {nodes.Count} 个节点" : null,
            TargetType: "panel");
    }

    public async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        var node = context.Node;
        var stopwatch = Stopwatch.StartNew();

        var precheck = await _updates.PrecheckAsync([node.Id], cancellationToken).ConfigureAwait(false);
        var item = precheck.Items.FirstOrDefault();
        if (item is null)
        {
            return Failure(node, "预检未返回该节点信息，已停止升级。", stopwatch.Elapsed, null);
        }

        if (!item.CanUpgrade)
        {
            return Failure(
                node,
                "节点不满足升级条件：" + string.Join("、", item.BlockingReasons) + "。",
                stopwatch.Elapsed,
                item.CurrentVersion);
        }

        var target = context.Parameters.GetString("targetVersion")?.Trim() ?? item.TargetVersion;
        if (string.IsNullOrWhiteSpace(target))
        {
            return Failure(node, "无法确定目标版本，已停止升级。", stopwatch.Elapsed, item.CurrentVersion);
        }

        if (!PanelVersionComparer.TryParse(target, out _))
        {
            return Failure(node, $"目标版本格式不合法：{target}。", stopwatch.Elapsed, item.CurrentVersion);
        }

        if (item.CurrentVersion is { } current
            && PanelVersionComparer.Compare(target, current) <= 0)
        {
            return Failure(
                node,
                $"目标版本 {target} 不高于当前版本 {current}，已停止升级。",
                stopwatch.Elapsed,
                current,
                targetVersion: target);
        }

        var submit = await context.Client
            .PostAsync<PanelUpdateAckDto>(UpdateRoutes.UpgradeExecute, new { version = target }, cancellationToken)
            .ConfigureAwait(false);

        if (submit.Error is not null)
        {
            _logger.LogWarning(
                "面板升级命令提交失败 节点 {NodeId} 目标版本 {TargetVersion}",
                node.Id,
                target);
            return Failure(
                node,
                $"提交升级命令失败：{submit.Error.Message}",
                stopwatch.Elapsed,
                item.CurrentVersion,
                submit.Error,
                target);
        }

        var probe = await WaitForHealthyAsync(context.Client, target, cancellationToken).ConfigureAwait(false);
        var detail = BuildDetail(item.CurrentVersion, target, probe);

        if (probe.VersionReached)
        {
            var elapsedSeconds = probe.Duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            _logger.LogInformation(
                "面板升级成功 节点 {NodeId} {FromVersion} -> {TargetVersion} 健康检查耗时 {ElapsedSeconds}s",
                node.Id,
                item.CurrentVersion,
                target,
                elapsedSeconds);

            return new OperationStepResult(
                node.Id,
                node.Name,
                Success: true,
                Message: $"升级成功：{item.CurrentVersion ?? "未知版本"} → {probe.ObservedVersion}（面板已恢复并通过健康检查，耗时 {elapsedSeconds} 秒）。",
                TaskId: null,
                DetailJson: detail.ToJsonString(),
                Error: null,
                Duration: stopwatch.Elapsed,
                TargetId: node.Id.Value.ToString());
        }

        var timeoutSeconds = _options.HealthCheckTimeoutSeconds;
        string message;
        PanelError error;
        if (probe.Reachable)
        {
            message = $"升级命令已提交，但 {timeoutSeconds} 秒内未回读到目标版本 {target}"
                + $"（最近回读：{probe.ObservedVersion ?? probe.VersionError ?? "未知"}）。"
                + "升级可能仍在进行或已失败，请人工登录节点确认；PanelFleet 不会自动回滚或重试。";
            error = new PanelError(PanelErrorKind.Protocol, message);
        }
        else
        {
            message = $"升级命令已提交，但 {timeoutSeconds} 秒内面板不可达"
                + $"（{probe.LastError?.Message ?? "无响应"}）。面板可能仍在重启，请稍后用版本检查确认；PanelFleet 不会自动回滚或重试。";
            error = probe.LastError ?? new PanelError(PanelErrorKind.Timeout, message);
        }

        _logger.LogWarning(
            "面板升级后健康检查未通过 节点 {NodeId} 目标版本 {TargetVersion} 可达 {Reachable}",
            node.Id,
            target,
            probe.Reachable);

        return new OperationStepResult(
            node.Id,
            node.Name,
            Success: false,
            Message: message,
            TaskId: null,
            DetailJson: detail.ToJsonString(),
            Error: error,
            Duration: stopwatch.Elapsed,
            TargetId: node.Id.Value.ToString());
    }

    private async Task<HealthProbeResult> WaitForHealthyAsync(
        IPanelClient client,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(_options.HealthCheckTimeoutSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.HealthCheckIntervalSeconds));
        var checks = 0;
        var reachable = false;
        string? observedVersion = null;
        string? versionError = null;
        PanelError? lastError = null;

        while (stopwatch.Elapsed < timeout)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            checks++;

            var health = await client.GetDashboardBaseAsync("all", "all", cancellationToken).ConfigureAwait(false);
            if (health.Error is not null)
            {
                lastError = health.Error;
                continue;
            }

            reachable = true;

            var version = await client
                .PostAsync<PanelSettingBaseDto>(UpdateRoutes.PanelSettingBase, new { }, cancellationToken)
                .ConfigureAwait(false);
            if (version.Error is not null)
            {
                versionError = version.Error.Message;
                continue;
            }

            observedVersion = version.Data?.SystemVersion?.Trim();
            if (!string.IsNullOrWhiteSpace(observedVersion)
                && PanelVersionComparer.Compare(observedVersion, targetVersion) >= 0)
            {
                return new HealthProbeResult(
                    VersionReached: true,
                    Reachable: true,
                    ObservedVersion: observedVersion,
                    VersionError: null,
                    LastError: null,
                    Checks: checks,
                    Duration: stopwatch.Elapsed);
            }
        }

        return new HealthProbeResult(
            VersionReached: false,
            Reachable: reachable,
            ObservedVersion: observedVersion,
            VersionError: versionError,
            LastError: lastError,
            Checks: checks,
            Duration: stopwatch.Elapsed);
    }

    private static JsonObject BuildDetail(string? currentVersion, string targetVersion, HealthProbeResult probe)
    {
        var phases = new JsonArray { "submitted" };
        if (probe.Reachable)
        {
            phases.Add("restarted");
        }

        if (probe.VersionReached)
        {
            phases.Add("healthy");
        }

        return new JsonObject
        {
            ["currentVersion"] = currentVersion,
            ["targetVersion"] = targetVersion,
            ["observedVersion"] = probe.ObservedVersion,
            ["panelReachable"] = probe.Reachable,
            ["healthChecks"] = probe.Checks,
            ["healthDurationMs"] = (long)probe.Duration.TotalMilliseconds,
            ["phases"] = phases
        };
    }

    private static OperationStepResult Failure(
        NodeRecord node,
        string message,
        TimeSpan duration,
        string? currentVersion,
        PanelError? error = null,
        string? targetVersion = null)
    {
        var detail = new JsonObject
        {
            ["currentVersion"] = currentVersion,
            ["targetVersion"] = targetVersion,
            ["phases"] = new JsonArray { "precheck" }
        };

        return new OperationStepResult(
            node.Id,
            node.Name,
            Success: false,
            Message: message,
            TaskId: null,
            DetailJson: detail.ToJsonString(),
            Error: error,
            Duration: duration,
            TargetId: node.Id.Value.ToString());
    }

    private sealed record HealthProbeResult(
        bool VersionReached,
        bool Reachable,
        string? ObservedVersion,
        string? VersionError,
        PanelError? LastError,
        int Checks,
        TimeSpan Duration);
}
