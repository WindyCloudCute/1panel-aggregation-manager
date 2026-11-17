using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Websites;

/// <summary>写操作目标：网站 ID 与展示名（主域名）。</summary>
public sealed record WebsiteTarget(long WebsiteId, string? Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"网站 #{WebsiteId.ToString(CultureInfo.InvariantCulture)}"
        : Name.Trim();
}

/// <summary>网站管理写操作参数解析与校验（供处理器与单元测试复用）。</summary>
public static class WebsiteOperationParameters
{
    private const int MaxRemarkLength = 128;

    private static readonly Regex DomainPattern =
        new("^[A-Za-z0-9*][A-Za-z0-9*._-]{0,252}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProxyNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static WebsiteTarget ReadTarget(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var websiteId = ReadInt64(parameters, "websiteId") ?? ReadInt64(parameters, "id") ?? 0;
        if (websiteId <= 0)
        {
            throw OperationValidationException.Missing("网站 ID（websiteId）");
        }

        return new WebsiteTarget(websiteId, OptionalString(parameters, "name"));
    }

    public static long RequireInt64(OperationParameters parameters, string name, string label)
    {
        var value = ReadInt64(parameters, name) ?? 0;
        return value > 0 ? value : throw OperationValidationException.Missing(label);
    }

    public static long? OptionalInt64(OperationParameters parameters, string name)
    {
        var value = ReadInt64(parameters, name);
        return value is > 0 ? value : null;
    }

    public static string RequireString(OperationParameters parameters, string name, string label, int maxLength = 255)
    {
        var value = OptionalString(parameters, name);
        if (value is null)
        {
            throw OperationValidationException.Missing(label);
        }

        return value.Length <= maxLength
            ? value
            : throw new OperationValidationException($"{label}长度不能超过 {maxLength} 个字符。");
    }

    public static string? OptionalString(OperationParameters parameters, string name, int maxLength = 1024)
    {
        var value = parameters.GetString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value.Length <= maxLength
            ? value
            : throw new OperationValidationException($"参数 {name} 长度不能超过 {maxLength} 个字符。");
    }

    public static bool GetBoolean(OperationParameters parameters, string name, bool fallback = false) =>
        parameters.GetBoolean(name, fallback);

    public static string ValidateDomain(string domain)
    {
        var value = domain.Trim();
        if (value.Length > 253 || !DomainPattern.IsMatch(value) || value.Contains("://", StringComparison.Ordinal))
        {
            throw new OperationValidationException("域名格式不合法：只允许字母、数字、*、-、_、.。");
        }

        return value;
    }

    public static int ValidatePort(int port, int fallback = 80)
    {
        var value = port <= 0 ? fallback : port;
        return value is > 0 and <= 65535
            ? value
            : throw new OperationValidationException("端口必须在 1~65535 之间。");
    }

    public static string ValidateProxyName(string name)
    {
        var value = name.Trim();
        return ProxyNamePattern.IsMatch(value)
            ? value
            : throw new OperationValidationException("代理名称只允许字母、数字、-、_、.，且不超过 64 个字符。");
    }

    public static string ValidateProxyMatch(string? match)
    {
        var value = string.IsNullOrWhiteSpace(match) ? "/" : match.Trim();
        return value.StartsWith('/')
            ? value
            : throw new OperationValidationException("代理路径（match）必须以 / 开头。");
    }

    public static string ValidateProxyPass(string proxyPass)
    {
        var value = proxyPass.Trim();
        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? value
            : throw new OperationValidationException("代理目标（proxyPass）必须是 http:// 或 https:// 地址。");
    }

    public static string ValidateHttpConfig(string? httpConfig)
    {
        var value = string.IsNullOrWhiteSpace(httpConfig) ? "HTTPToHTTPS" : httpConfig.Trim();
        return value is "HTTPSOnly" or "HTTPAlso" or "HTTPToHTTPS"
            ? value
            : throw new OperationValidationException("HTTPS 访问方式只支持 HTTPSOnly、HTTPAlso、HTTPToHTTPS。");
    }

    /// <summary>备注最长 128 字符（1Panel 页面限制）。</summary>
    public static string? OptionalRemark(OperationParameters parameters)
    {
        var remark = OptionalString(parameters, "remark", MaxRemarkLength);
        return remark;
    }

    private static long? ReadInt64(OperationParameters parameters, string name)
    {
        if (parameters.GetInt64(name) is { } value)
        {
            return value;
        }

        return long.TryParse(parameters.GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>
/// 网站管理写操作处理器基类：解析目标 → 预览（标题/摘要/风险/警告）→ 提交 1Panel 写接口。
/// <para>单节点风险由子类声明；多节点（批量）统一提升为 Critical，强确认文本由内核生成。</para>
/// </summary>
public abstract class WebsiteOperationHandlerBase : IOperationHandler
{
    public abstract string Kind { get; }

    /// <summary>标题前缀，如「启动网站」。</summary>
    protected abstract string TitlePrefix { get; }

    /// <summary>单节点风险等级。</summary>
    protected abstract OperationRisk SingleNodeRisk { get; }

    /// <summary>审计目标类型（默认 website）。</summary>
    protected virtual string TargetType => "website";

    public virtual Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = WebsiteOperationParameters.ReadTarget(context.Parameters);
        ValidatePlan(context, target);

        var risk = context.Nodes.Count > 1 ? OperationRisk.Critical : SingleNodeRisk;
        var warnings = new List<string>
        {
            "写操作不会自动回滚；执行结果以 1Panel 实际配置为准。"
        };

        if (context.Nodes.Count > 1)
        {
            warnings.Add("批量操作按节点顺序执行，某节点失败后停止后续节点；已执行的节点不会回滚。");
        }

        warnings.AddRange(BuildWarnings(context, target));
        if (risk == OperationRisk.High)
        {
            warnings.Add("该操作风险较高，请核对网站与目标后再确认执行。");
        }

        return Task.FromResult(new OperationPlan(
            $"{TitlePrefix} {target.DisplayName}",
            BuildSummary(context, target),
            risk,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            TargetType: TargetType));
    }

    public abstract Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken);

    /// <summary>预览阶段的额外参数校验（抛 <see cref="OperationValidationException"/>）。</summary>
    protected virtual void ValidatePlan(OperationPlanContext context, WebsiteTarget target)
    {
    }

    /// <summary>预览阶段的操作警告。</summary>
    protected virtual IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) => [];

    protected abstract string BuildSummary(OperationPlanContext context, WebsiteTarget target);

    /// <summary>执行结果统一映射；<paramref name="detail"/> 只放键名/开关，不放敏感值。</summary>
    protected static OperationStepResult FromCall(
        OperationNodeContext context,
        PanelCallResult<JsonElement> result,
        string successMessage,
        JsonObject? detail,
        string targetId)
    {
        if (!result.IsSuccess)
        {
            var error = result.Error ?? new PanelError(PanelErrorKind.Protocol, "1Panel 未返回错误信息。");
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                error.Message,
                null,
                detail?.ToJsonString(),
                error,
                TimeSpan.Zero,
                targetId);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            successMessage,
            null,
            detail?.ToJsonString(),
            null,
            TimeSpan.Zero,
            targetId);
    }

    protected static string WebsiteTargetId(long websiteId) =>
        websiteId.ToString(CultureInfo.InvariantCulture);
}
