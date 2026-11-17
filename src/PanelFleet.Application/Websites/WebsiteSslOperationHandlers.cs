using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.Websites;

/// <summary>申请 SSL 证书（<c>ssl.apply</c>）：Medium；调用 <c>POST websites/ssl/obtain</c>。</summary>
public sealed class SslApplyHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "ssl.apply";

    protected override string TitlePrefix => "申请 SSL 证书";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override string TargetType => "ssl";

    private sealed record Arguments(long SslId, string? PrimaryDomain, bool SkipDnsCheck);

    /// <summary>证书申请不绑定某个网站（目标为证书），因此不使用基类的网站目标解析。</summary>
    public override Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = Read(context.Parameters);
        var certificate = arguments.PrimaryDomain ?? $"证书 #{arguments.SslId.ToString(CultureInfo.InvariantCulture)}";
        var risk = context.Nodes.Count > 1 ? OperationRisk.Critical : OperationRisk.Medium;

        var warnings = new List<string>
        {
            "证书申请依赖域名 DNS 正确解析与 1Panel 的 ACME 账户配置；可能耗时数分钟。",
            "若请求超时，证书申请可能仍在 1Panel 后台执行，请稍后刷新证书状态或查看 1Panel 任务日志。",
            "申请失败不会自动清理证书记录，可重新申请或删除证书。"
        };

        if (context.Nodes.Count > 1)
        {
            warnings.Add("批量申请按节点顺序执行，某节点失败后停止后续节点；已执行节点不会回滚。");
        }

        var summary = $"在 {context.Nodes.Count} 个节点为证书 {certificate} 发起申请"
            + $"（跳过 DNS 检查={Flag(arguments.SkipDnsCheck)}）。";

        return Task.FromResult(new OperationPlan(
            $"申请 SSL 证书 {certificate}",
            summary,
            risk,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            TargetType: "ssl"));
    }

    /// <summary>证书申请不使用基类网站目标，这里仅满足抽象契约（实际摘要由 <see cref="PlanAsync"/> 生成）。</summary>
    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target) =>
        $"在 {context.Nodes.Count} 个节点申请 SSL 证书。";

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = Read(context.Parameters);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ID"] = arguments.SslId,
            ["skipDNSCheck"] = arguments.SkipDnsCheck
        };

        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.SslObtain, body, cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"证书申请已提交（证书 ID {arguments.SslId.ToString(CultureInfo.InvariantCulture)}）；如为 ACME 申请请稍后刷新状态。",
            new JsonObject { ["ID"] = arguments.SslId, ["skipDNSCheck"] = arguments.SkipDnsCheck },
            arguments.SslId.ToString(CultureInfo.InvariantCulture));
    }

    private static Arguments Read(OperationParameters parameters)
    {
        var sslId = WebsiteOperationParameters.RequireInt64(parameters, "sslId", "SSL 证书 ID（sslId）");
        return new Arguments(
            sslId,
            WebsiteOperationParameters.OptionalString(parameters, "primaryDomain", 253),
            WebsiteOperationParameters.GetBoolean(parameters, "skipDnsCheck"));
    }

    private static string Flag(bool value) => value ? "是" : "否";
}

/// <summary>部署 SSL 证书到网站（<c>ssl.deploy</c>）：High；调用 <c>POST websites/{id}/https</c>。</summary>
public sealed class SslDeployHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "ssl.deploy";

    protected override string TitlePrefix => "部署 SSL 证书";

    protected override OperationRisk SingleNodeRisk => OperationRisk.High;

    private sealed record Arguments(
        long WebsiteId,
        long SslId,
        string? PrimaryDomain,
        string HttpConfig,
        IReadOnlyList<string> SslProtocols,
        bool Enable);

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) => _ = Read(context.Parameters);

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
    [
        "部署证书会修改 Nginx HTTPS 配置并重载，可能短暂中断网站访问，操作不可回滚。",
        "请确认证书域名与网站主域名匹配；如不匹配浏览器会提示证书错误。",
        "启用 HTTPS 后请确保节点 443 端口可访问，并在 1Panel 中检查 HTTP 跳转方式。"
    ];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = Read(context.Parameters);
        var certificate = arguments.PrimaryDomain ?? $"证书 #{arguments.SslId.ToString(CultureInfo.InvariantCulture)}";
        return $"在 {context.Nodes.Count} 个节点把 {certificate} 部署到网站 {target.DisplayName}"
            + $"（HTTPS={Flag(arguments.Enable)}，访问方式={arguments.HttpConfig}）。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = Read(context.Parameters);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["websiteId"] = arguments.WebsiteId,
            ["enable"] = arguments.Enable,
            ["websiteSSLId"] = arguments.SslId,
            ["type"] = "existed",
            ["httpConfig"] = arguments.HttpConfig,
            ["SSLProtocol"] = arguments.SslProtocols,
            ["algorithm"] = string.Empty,
            ["hsts"] = false,
            ["hstsIncludeSubDomains"] = false,
            ["http3"] = false
        };

        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.Https(arguments.WebsiteId), body, cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"证书部署指令已提交（网站 ID {arguments.WebsiteId.ToString(CultureInfo.InvariantCulture)}，证书 ID {arguments.SslId.ToString(CultureInfo.InvariantCulture)}）。",
            new JsonObject
            {
                ["websiteId"] = arguments.WebsiteId,
                ["websiteSSLId"] = arguments.SslId,
                ["httpConfig"] = arguments.HttpConfig,
                ["enable"] = arguments.Enable
            },
            WebsiteTargetId(arguments.WebsiteId));
    }

    private static Arguments Read(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var sslId = WebsiteOperationParameters.RequireInt64(parameters, "sslId", "SSL 证书 ID（sslId）");
        var httpConfig = WebsiteOperationParameters.ValidateHttpConfig(parameters.GetString("httpConfig"));
        var protocols = parameters.GetStringArray("SSLProtocol");
        if (protocols.Count == 0)
        {
            protocols = parameters.GetStringArray("sslProtocols");
        }

        if (protocols.Count == 0)
        {
            protocols = ["TLSv1.2", "TLSv1.3"];
        }

        return new Arguments(
            target.WebsiteId,
            sslId,
            WebsiteOperationParameters.OptionalString(parameters, "primaryDomain", 253)
                ?? WebsiteOperationParameters.OptionalString(parameters, "name", 253),
            httpConfig,
            protocols,
            WebsiteOperationParameters.GetBoolean(parameters, "enable", fallback: true));
    }

    private static string Flag(bool value) => value ? "是" : "否";
}
