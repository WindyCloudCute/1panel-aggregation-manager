using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.Websites;

/// <summary>网站启动（<c>website.start</c>）：Medium；调用 <c>POST websites/operate</c>（operate=start）。</summary>
public sealed class WebsiteStartHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "website.start";

    protected override string TitlePrefix => "启动网站";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["启动会重新加载 Nginx 配置并启动网站服务；如配置有误可能启动失败，请在结果中查看 1Panel 返回信息。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target) =>
        $"在 {context.Nodes.Count} 个节点启动网站 {target.DisplayName}。";

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = WebsiteOperationParameters.ReadTarget(context.Parameters);
        var result = await context.Client
            .PostAsync<JsonElement>(
                WebsiteRoutes.Operate,
                new Dictionary<string, object?>
                {
                    ["id"] = target.WebsiteId,
                    ["operate"] = "start"
                },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"网站启动指令已提交（{target.DisplayName}）。",
            new JsonObject { ["id"] = target.WebsiteId, ["operate"] = "start" },
            WebsiteTargetId(target.WebsiteId));
    }
}

/// <summary>网站停止（<c>website.stop</c>）：Medium；调用 <c>POST websites/operate</c>（operate=stop）。</summary>
public sealed class WebsiteStopHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "website.stop";

    protected override string TitlePrefix => "停止网站";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["停止后网站将不可访问，直到重新启动；不会自动回滚。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target) =>
        $"在 {context.Nodes.Count} 个节点停止网站 {target.DisplayName}。";

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = WebsiteOperationParameters.ReadTarget(context.Parameters);
        var result = await context.Client
            .PostAsync<JsonElement>(
                WebsiteRoutes.Operate,
                new Dictionary<string, object?>
                {
                    ["id"] = target.WebsiteId,
                    ["operate"] = "stop"
                },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"网站停止指令已提交（{target.DisplayName}）。",
            new JsonObject { ["id"] = target.WebsiteId, ["operate"] = "stop" },
            WebsiteTargetId(target.WebsiteId));
    }
}

/// <summary>更新网站基础信息（<c>website.update</c>）：Medium；域名/备注/分组/到期/收藏。</summary>
public sealed class WebsiteUpdateHandler : WebsiteOperationHandlerBase
{
    private sealed record UpdateArguments(
        long WebsiteId,
        string PrimaryDomain,
        string? Remark,
        long GroupId,
        string ExpireDate,
        bool Favorite,
        bool IpV6,
        string? Name);

    public override string Kind => "website.update";

    protected override string TitlePrefix => "修改网站";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) => _ = ReadArguments(context.Parameters);

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["修改主域名会影响网站访问地址与已部署证书的域名匹配，请确认域名已在 DNS 中解析到目标节点。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ReadArguments(context.Parameters);
        var favorite = arguments.Favorite ? "是" : "否";
        return $"在 {context.Nodes.Count} 个节点把网站 {target.DisplayName} 更新为 {arguments.PrimaryDomain}"
            + $"（分组 ID {arguments.GroupId.ToString(CultureInfo.InvariantCulture)}，到期 {arguments.ExpireDate}，收藏 {favorite}）。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = arguments.WebsiteId,
            ["primaryDomain"] = arguments.PrimaryDomain,
            ["remark"] = arguments.Remark ?? string.Empty,
            ["webSiteGroupId"] = arguments.GroupId,
            ["expireDate"] = arguments.ExpireDate,
            ["IPV6"] = arguments.IpV6,
            ["favorite"] = arguments.Favorite
        };

        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.Update, body, cancellationToken)
            .ConfigureAwait(false);

        var detail = new JsonObject
        {
            ["id"] = arguments.WebsiteId,
            ["primaryDomain"] = arguments.PrimaryDomain,
            ["webSiteGroupId"] = arguments.GroupId,
            ["favorite"] = arguments.Favorite,
            ["IPV6"] = arguments.IpV6
        };

        return FromCall(
            context,
            result,
            $"网站信息已更新（{arguments.PrimaryDomain}）。",
            detail,
            WebsiteTargetId(arguments.WebsiteId));
    }

    private static UpdateArguments ReadArguments(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var primaryDomain = WebsiteOperationParameters.ValidateDomain(
            WebsiteOperationParameters.RequireString(parameters, "primaryDomain", "主域名（primaryDomain）", 253));

        var groupId = WebsiteOperationParameters.OptionalInt64(parameters, "webSiteGroupId")
            ?? WebsiteOperationParameters.OptionalInt64(parameters, "groupId")
            ?? 0;

        var expireDate = WebsiteOperationParameters.OptionalString(parameters, "expireDate", 32) ?? "9999-12-31";
        if (!string.Equals(expireDate, "9999-12-31", StringComparison.Ordinal)
            && !DateTime.TryParseExact(
                expireDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new OperationValidationException("到期日期格式必须为 yyyy-MM-dd，或使用 9999-12-31 表示永久。");
        }

        return new UpdateArguments(
            target.WebsiteId,
            primaryDomain,
            WebsiteOperationParameters.OptionalRemark(parameters),
            groupId,
            expireDate,
            WebsiteOperationParameters.GetBoolean(parameters, "favorite"),
            WebsiteOperationParameters.GetBoolean(parameters, "IPV6"),
            target.Name);
    }
}

/// <summary>切换网站 PHP 版本（<c>website.php_version</c>）：Medium；调用 <c>POST websites/php/version</c>。</summary>
public sealed class WebsitePhpVersionHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "website.php_version";

    protected override string TitlePrefix => "切换 PHP 版本";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) =>
        _ = WebsiteOperationParameters.RequireInt64(context.Parameters, "runtimeId", "PHP 运行环境 ID（runtimeId）");

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["切换 PHP 版本会重载网站运行环境，可能短暂中断服务；请确认目标 PHP 版本已安装并处于运行状态。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var runtimeId = WebsiteOperationParameters.RequireInt64(context.Parameters, "runtimeId", "PHP 运行环境 ID（runtimeId）");
        return $"在 {context.Nodes.Count} 个节点把网站 {target.DisplayName} 切换到 PHP 运行环境 #{runtimeId.ToString(CultureInfo.InvariantCulture)}。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = WebsiteOperationParameters.ReadTarget(context.Parameters);
        var runtimeId = WebsiteOperationParameters.RequireInt64(context.Parameters, "runtimeId", "PHP 运行环境 ID（runtimeId）");

        var result = await context.Client
            .PostAsync<JsonElement>(
                "api/v2/websites/php/version",
                new Dictionary<string, object?>
                {
                    ["websiteID"] = target.WebsiteId,
                    ["runtimeID"] = runtimeId
                },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"PHP 版本切换指令已提交（{target.DisplayName} → 运行环境 #{runtimeId.ToString(CultureInfo.InvariantCulture)}）。",
            new JsonObject { ["websiteID"] = target.WebsiteId, ["runtimeID"] = runtimeId },
            WebsiteTargetId(target.WebsiteId));
    }
}
