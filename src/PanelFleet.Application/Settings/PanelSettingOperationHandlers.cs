using System.Diagnostics;
using System.Text.Json.Nodes;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Settings;

namespace PanelFleet.Application.Settings;

/// <summary>
/// 修改 1Panel 面板基础设置（目前支持面板名称、面板终端按钮开关）。
/// <para>风险等级 Medium：两项修改都不会重启面板；端口/监听地址/SSL/安全入口不在此操作内，需在面板手工修改。</para>
/// </summary>
public sealed class PanelSettingUpdateHandler : IOperationHandler
{
    /// <summary>操作类型标识。</summary>
    public const string HandlerKind = "panel.setting.update";

    /// <inheritdoc />
    public string Kind => HandlerKind;

    /// <inheritdoc />
    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var panelName = context.Parameters.GetString("panelName")?.Trim();
        var hasPanelName = !string.IsNullOrEmpty(panelName);
        var hasTerminal = context.Parameters.Contains("terminalEnabled");

        if (!hasPanelName && !hasTerminal)
        {
            throw new OperationValidationException("至少提供 panelName（面板名称）或 terminalEnabled（终端开关）之一。");
        }

        if (hasPanelName)
        {
            if (panelName!.Length > 64)
            {
                throw new OperationValidationException("面板名称不能超过 64 个字符。");
            }

            if (panelName.Any(char.IsControl))
            {
                throw new OperationValidationException("面板名称不能包含控制字符。");
            }
        }

        var changed = new List<string>();
        if (hasPanelName)
        {
            changed.Add($"面板名称 → {panelName}");
        }

        if (hasTerminal)
        {
            changed.Add($"面板终端按钮 → {EnableText(context.Parameters.GetBoolean("terminalEnabled", false))}");
        }

        var targets = context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray();
        var warnings = new List<string>
        {
            "本次不会修改端口、监听地址、安全入口与 SSL；这些项需在 1Panel 面板操作，修改会重启面板并断开会话。",
            "修改面板名称不会影响 PanelFleet 的连接配置。"
        };

        return Task.FromResult(new OperationPlan(
            "修改面板设置",
            "修改 1Panel 面板基础设置：" + string.Join("；", changed) + "。",
            OperationRisk.Medium,
            targets,
            warnings,
            TargetType: "panel"));
    }

    /// <inheritdoc />
    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();
        var node = context.Node;
        var detail = new JsonObject();

        var panelName = context.Parameters.GetString("panelName")?.Trim();
        var hasPanelName = !string.IsNullOrEmpty(panelName);
        var hasTerminal = context.Parameters.Contains("terminalEnabled");
        var terminalEnabled = context.Parameters.GetBoolean("terminalEnabled", false);

        if (hasPanelName)
        {
            var result = await context.Client.PostAsync<PanelSettingsAckDto>(
                SettingsRoutes.CoreSettingsUpdate,
                new PanelSettingUpdateRequest { Key = "PanelName", Value = panelName! },
                cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                return Failure(node, $"更新面板名称失败：{result.Error.Message}", stopwatch, detail);
            }

            detail["panelName"] = panelName;
        }

        if (hasTerminal)
        {
            var result = await context.Client.PostAsync<PanelSettingsAckDto>(
                SettingsRoutes.CoreTerminalUpdate,
                new PanelTerminalUpdateRequest { ShowTerminalButton = terminalEnabled ? "Enable" : "Disable" },
                cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                return Failure(node, $"更新面板终端开关失败：{result.Error.Message}", stopwatch, detail);
            }

            detail["terminalEnabled"] = terminalEnabled;
        }

        stopwatch.Stop();
        return new OperationStepResult(
            node.Id,
            node.Name,
            true,
            "面板设置已更新。",
            null,
            detail.ToJsonString(),
            null,
            stopwatch.Elapsed,
            node.Id.Value.ToString());
    }

    private static OperationStepResult Failure(NodeRecord node, string message, Stopwatch stopwatch, JsonObject detail)
    {
        stopwatch.Stop();
        return new OperationStepResult(
            node.Id,
            node.Name,
            false,
            message,
            null,
            detail.ToJsonString(),
            null,
            stopwatch.Elapsed,
            node.Id.Value.ToString());
    }

    private static string EnableText(bool enabled) => enabled ? "已开启" : "已关闭";
}

/// <summary>
/// 切换 1Panel API 接口开关。
/// <para>风险等级 High：会改变 PanelFleet 的接入能力，需要显式输入确认文本；单次仅允许一个节点。</para>
/// </summary>
public sealed class PanelApiConfigHandler : IOperationHandler
{
    /// <summary>操作类型标识。</summary>
    public const string HandlerKind = "panel.api.update";

    /// <inheritdoc />
    public string Kind => HandlerKind;

    /// <inheritdoc />
    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Nodes.Count != 1)
        {
            throw new OperationValidationException("API 接口开关属于高风险操作，单次只能选择一个节点。");
        }

        var status = NormalizeStatus(context.Parameters.RequireString("apiInterfaceStatus", "API 接口开关"));

        var warnings = new List<string>
        {
            "关闭 API 接口后 PanelFleet 将立即失去该节点的数据访问能力，需要到 1Panel 面板重新开启。",
            "本操作不会重置 API 密钥，密钥保持不变。"
        };

        return Task.FromResult(new OperationPlan(
            $"{(status == "Enable" ? "开启" : "关闭")} API 接口",
            $"将 1Panel 的 API 接口切换为 {EnableText(status)}。",
            OperationRisk.High,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            TypedConfirmationText: $"{(status == "Enable" ? "开启" : "关闭")} API 接口",
            TargetType: "panel"));
    }

    /// <inheritdoc />
    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();
        var node = context.Node;
        var detail = new JsonObject();

        string status;
        try
        {
            status = NormalizeStatus(context.Parameters.RequireString("apiInterfaceStatus", "API 接口开关"));
        }
        catch (OperationValidationException exception)
        {
            return Failure(node, exception.Message, stopwatch, detail);
        }

        var current = await context.Client.GetAsync<PanelCurrentUserDto>(
            SettingsRoutes.CoreCurrentUser,
            cancellationToken).ConfigureAwait(false);
        if (current.Error is not null || current.Data is null)
        {
            var message = current.Error is not null
                ? $"读取 API 接口状态失败：{current.Error.Message}"
                : "读取 API 接口状态失败：面板未返回当前用户信息。";
            return Failure(node, message, stopwatch, detail);
        }

        var request = new PanelApiConfigUpdateRequest
        {
            ApiInterfaceStatus = status,
            ApiKey = current.Data.ApiKey,
            IpWhiteList = current.Data.IpWhiteList,
            ApiTrustedProxies = current.Data.ApiTrustedProxies,
            ApiKeyValidityTime = current.Data.ApiKeyValidityTime
        };

        var result = await context.Client.PostAsync<PanelSettingsAckDto>(
            SettingsRoutes.CoreApiConfigUpdate,
            request,
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return Failure(node, $"切换 API 接口失败：{result.Error.Message}", stopwatch, detail);
        }

        detail["apiInterfaceStatus"] = status;
        stopwatch.Stop();
        return new OperationStepResult(
            node.Id,
            node.Name,
            true,
            $"API 接口已 {EnableText(status)}。",
            null,
            detail.ToJsonString(),
            null,
            stopwatch.Elapsed,
            node.Id.Value.ToString());
    }

    private static string NormalizeStatus(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "enable" or "enabled" or "on" or "true" => "Enable",
        "disable" or "disabled" or "off" or "false" => "Disable",
        _ => throw new OperationValidationException("API 接口开关取值只能是 Enable 或 Disable。")
    };

    private static string EnableText(string status) => status == "Enable" ? "开启" : "关闭";

    private static OperationStepResult Failure(NodeRecord node, string message, Stopwatch stopwatch, JsonObject detail)
    {
        stopwatch.Stop();
        return new OperationStepResult(
            node.Id,
            node.Name,
            false,
            message,
            null,
            detail.ToJsonString(),
            null,
            stopwatch.Elapsed,
            node.Id.Value.ToString());
    }
}

/// <summary>
/// 修改 1Panel 面板登录密码（需旧密码 + 新密码）。
/// <para>风险等级 High：参数值不会写入审计、不会出现在操作记录与日志中；单次仅允许一个节点。</para>
/// </summary>
public sealed class PanelPasswordUpdateHandler : IOperationHandler
{
    /// <summary>操作类型标识。</summary>
    public const string HandlerKind = "panel.password.update";

    /// <inheritdoc />
    public string Kind => HandlerKind;

    /// <inheritdoc />
    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Nodes.Count != 1)
        {
            throw new OperationValidationException("修改面板密码属于高风险操作，单次只能选择一个节点。");
        }

        var oldPassword = context.Parameters.RequireString("oldPassword", "旧密码");
        var newPassword = context.Parameters.RequireString("newPassword", "新密码");
        ValidatePasswords(oldPassword, newPassword);

        var warnings = new List<string>
        {
            "修改后请立即在 1Panel 面板使用新密码登录确认；旧密码将失效。",
            "面板密码用于面板登录，与 PanelFleet 使用的 API Key 无关，连接不会中断。",
            "密码不会写入审计与日志，也不会在预览与执行结果中回显。"
        };

        return Task.FromResult(new OperationPlan(
            "修改面板密码",
            "修改 1Panel 面板登录密码（需要旧密码校验）。",
            OperationRisk.High,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            TypedConfirmationText: "修改密码",
            TargetType: "panel"));
    }

    /// <inheritdoc />
    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();
        var node = context.Node;
        var detail = new JsonObject();

        string oldPassword;
        string newPassword;
        try
        {
            oldPassword = context.Parameters.RequireString("oldPassword", "旧密码");
            newPassword = context.Parameters.RequireString("newPassword", "新密码");
            ValidatePasswords(oldPassword, newPassword);
        }
        catch (OperationValidationException exception)
        {
            return Failure(node, exception.Message, stopwatch, detail);
        }

        var current = await context.Client.GetAsync<PanelCurrentUserDto>(
            SettingsRoutes.CoreCurrentUser,
            cancellationToken).ConfigureAwait(false);
        var userName = current.Data?.Name;
        if (current.Error is not null || string.IsNullOrWhiteSpace(userName))
        {
            var message = current.Error is not null
                ? $"读取当前用户失败：{current.Error.Message}"
                : "读取当前用户失败：面板未返回用户名。";
            return Failure(node, message, stopwatch, detail);
        }

        var result = await context.Client.PostAsync<PanelSettingsAckDto>(
            SettingsRoutes.CoreCurrentUserUpdate,
            new PanelCurrentUserUpdateRequest
            {
                Name = userName!,
                OldPassword = oldPassword,
                Password = newPassword
            },
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return Failure(node, $"修改面板密码失败：{result.Error.Message}", stopwatch, detail);
        }

        detail["passwordChanged"] = true;
        stopwatch.Stop();
        return new OperationStepResult(
            node.Id,
            node.Name,
            true,
            "面板密码已修改，请使用新密码登录 1Panel 确认。",
            null,
            detail.ToJsonString(),
            null,
            stopwatch.Elapsed,
            node.Id.Value.ToString());
    }

    private static void ValidatePasswords(string oldPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(oldPassword))
        {
            throw new OperationValidationException("旧密码不能为空。");
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            throw new OperationValidationException("新密码不能为空。");
        }

        if (newPassword.Length < 8)
        {
            throw new OperationValidationException("新密码长度至少 8 位。");
        }

        if (newPassword.Length > 128)
        {
            throw new OperationValidationException("新密码长度不能超过 128 位。");
        }

        if (string.Equals(oldPassword, newPassword, StringComparison.Ordinal))
        {
            throw new OperationValidationException("新密码不能与旧密码相同。");
        }
    }

    private static OperationStepResult Failure(NodeRecord node, string message, Stopwatch stopwatch, JsonObject detail)
    {
        stopwatch.Stop();
        return new OperationStepResult(
            node.Id,
            node.Name,
            false,
            message,
            null,
            detail.ToJsonString(),
            null,
            stopwatch.Elapsed,
            node.Id.Value.ToString());
    }
}
