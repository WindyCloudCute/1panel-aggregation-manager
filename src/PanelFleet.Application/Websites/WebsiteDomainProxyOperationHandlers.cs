using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;

namespace PanelFleet.Application.Websites;

/// <summary>新增网站域名（<c>domain.create</c>）：Medium；调用 <c>POST websites/domains</c>。</summary>
public sealed class DomainCreateHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "domain.create";

    protected override string TitlePrefix => "新增网站域名";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target)
    {
        _ = ReadArguments(context.Parameters);
    }

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
    [
        "新增域名后需要确保 DNS 已解析到目标节点，否则访问与 HTTPS 证书申请会失败。",
        "域名端口默认 80；如需 HTTPS，请在证书部署后开启对应域名的 SSL 开关。"
    ];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ReadArguments(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点为网站 {target.DisplayName} 新增域名 {arguments.Domain}"
            + $"（端口 {arguments.Port.ToString(CultureInfo.InvariantCulture)}，SSL={Flag(arguments.Ssl)}）。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["websiteID"] = arguments.WebsiteId,
            ["domains"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["domain"] = arguments.Domain,
                    ["port"] = arguments.Port,
                    ["ssl"] = arguments.Ssl
                }
            }
        };

        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.DomainCreate, body, cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"域名 {arguments.Domain} 已添加到网站（端口 {arguments.Port.ToString(CultureInfo.InvariantCulture)}）。",
            new JsonObject
            {
                ["websiteID"] = arguments.WebsiteId,
                ["domain"] = arguments.Domain,
                ["port"] = arguments.Port,
                ["ssl"] = arguments.Ssl
            },
            WebsiteTargetId(arguments.WebsiteId));
    }

    private sealed record Arguments(long WebsiteId, string Domain, int Port, bool Ssl);

    private static Arguments ReadArguments(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var domain = WebsiteOperationParameters.ValidateDomain(
            WebsiteOperationParameters.RequireString(parameters, "domain", "域名（domain）", 253));
        var port = WebsiteOperationParameters.ValidatePort(parameters.GetInt32("port") ?? 80);
        return new Arguments(target.WebsiteId, domain, port, WebsiteOperationParameters.GetBoolean(parameters, "ssl"));
    }

    private static string Flag(bool value) => value ? "是" : "否";
}

/// <summary>更新域名 SSL 开关（<c>domain.update</c>）：Medium；调用 <c>POST websites/domains/update</c>。</summary>
public sealed class DomainUpdateHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "domain.update";

    protected override string TitlePrefix => "更新域名 SSL";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target)
    {
        _ = ReadArguments(context.Parameters);
    }

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["开启域名 SSL 前需要网站已部署有效证书；端口 80 的域名无法开启 SSL（1Panel 限制）。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ReadArguments(context.Parameters);
        var domain = arguments.DomainName ?? $"域名 #{arguments.DomainId.ToString(CultureInfo.InvariantCulture)}";
        return $"在 {context.Nodes.Count} 个节点把网站 {target.DisplayName} 的 {domain} SSL 开关设为 {Flag(arguments.Ssl)}。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);

        var result = await context.Client
            .PostAsync<JsonElement>(
                WebsiteRoutes.DomainUpdate,
                new Dictionary<string, object?>
                {
                    ["id"] = arguments.DomainId,
                    ["ssl"] = arguments.Ssl
                },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"域名 SSL 开关已更新（域名 ID {arguments.DomainId.ToString(CultureInfo.InvariantCulture)}，SSL={Flag(arguments.Ssl)}）。",
            new JsonObject { ["id"] = arguments.DomainId, ["ssl"] = arguments.Ssl },
            WebsiteTargetId(arguments.WebsiteId));
    }

    private sealed record Arguments(long WebsiteId, long DomainId, string? DomainName, bool Ssl);

    private static Arguments ReadArguments(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var domainId = WebsiteOperationParameters.RequireInt64(parameters, "domainId", "域名 ID（domainId）");
        var domainName = WebsiteOperationParameters.OptionalString(parameters, "domain", 253);
        return new Arguments(
            target.WebsiteId,
            domainId,
            domainName,
            WebsiteOperationParameters.GetBoolean(parameters, "ssl"));
    }

    private static string Flag(bool value) => value ? "是" : "否";
}

/// <summary>删除网站域名（<c>domain.delete</c>）：High；调用 <c>POST websites/domains/del</c>。</summary>
public sealed class DomainDeleteHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "domain.delete";

    protected override string TitlePrefix => "删除网站域名";

    protected override OperationRisk SingleNodeRisk => OperationRisk.High;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target)
    {
        _ = ReadArguments(context.Parameters);
    }

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
    [
        "删除域名后该域名不再指向此网站，访问会中断；操作不可回滚。",
        "1Panel 要求网站至少保留一个域名，删除前请确认还有其它可用域名。"
    ];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ReadArguments(context.Parameters);
        var domain = arguments.DomainName ?? $"域名 #{arguments.DomainId.ToString(CultureInfo.InvariantCulture)}";
        return $"在 {context.Nodes.Count} 个节点从网站 {target.DisplayName} 删除域名 {domain}（ID {arguments.DomainId.ToString(CultureInfo.InvariantCulture)}）。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);

        var result = await context.Client
            .PostAsync<JsonElement>(
                WebsiteRoutes.DomainDelete,
                new Dictionary<string, object?> { ["id"] = arguments.DomainId },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"域名删除指令已提交（域名 ID {arguments.DomainId.ToString(CultureInfo.InvariantCulture)}）。",
            new JsonObject { ["id"] = arguments.DomainId },
            WebsiteTargetId(arguments.WebsiteId));
    }

    private sealed record Arguments(long WebsiteId, long DomainId, string? DomainName);

    private static Arguments ReadArguments(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var domainId = WebsiteOperationParameters.RequireInt64(parameters, "domainId", "域名 ID（domainId）");
        return new Arguments(target.WebsiteId, domainId, WebsiteOperationParameters.OptionalString(parameters, "domain", 253));
    }
}

/// <summary>新增/编辑反向代理的公共参数。</summary>
internal sealed record ProxyArguments(
    long WebsiteId,
    string ProxyName,
    string Match,
    string ProxyPass,
    string ProxyHost,
    bool Enable,
    bool Cache,
    int CacheTime,
    string CacheUnit,
    int ServerCacheTime,
    string ServerCacheUnit,
    bool Sni,
    string? ProxySslName,
    bool SslVerify,
    bool Cors,
    string? AllowOrigins,
    string? AllowMethods,
    string? AllowHeaders,
    bool AllowCredentials,
    bool Preflight);

/// <summary>反向代理公共参数解析。</summary>
internal static class ProxyArgumentReader
{
    public static ProxyArguments Read(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var proxyName = WebsiteOperationParameters.ValidateProxyName(
            WebsiteOperationParameters.RequireString(parameters, "proxyName", "代理名称（proxyName）", 64));
        var match = WebsiteOperationParameters.ValidateProxyMatch(parameters.GetString("match"));
        var proxyPass = WebsiteOperationParameters.ValidateProxyPass(
            WebsiteOperationParameters.RequireString(parameters, "proxyPass", "代理目标（proxyPass）", 512));
        var proxyHost = WebsiteOperationParameters.OptionalString(parameters, "proxyHost", 255) ?? "$host";

        return new ProxyArguments(
            target.WebsiteId,
            proxyName,
            match,
            proxyPass,
            proxyHost,
            WebsiteOperationParameters.GetBoolean(parameters, "enable", fallback: true),
            WebsiteOperationParameters.GetBoolean(parameters, "cache"),
            parameters.GetInt32("cacheTime") ?? 0,
            WebsiteOperationParameters.OptionalString(parameters, "cacheUnit", 8) ?? "m",
            parameters.GetInt32("serverCacheTime") ?? 10,
            WebsiteOperationParameters.OptionalString(parameters, "serverCacheUnit", 8) ?? "m",
            WebsiteOperationParameters.GetBoolean(parameters, "sni"),
            WebsiteOperationParameters.OptionalString(parameters, "proxySSLName", 255),
            WebsiteOperationParameters.GetBoolean(parameters, "sslVerify"),
            WebsiteOperationParameters.GetBoolean(parameters, "cors"),
            WebsiteOperationParameters.OptionalString(parameters, "allowOrigins", 512),
            WebsiteOperationParameters.OptionalString(parameters, "allowMethods", 512),
            WebsiteOperationParameters.OptionalString(parameters, "allowHeaders", 512),
            WebsiteOperationParameters.GetBoolean(parameters, "allowCredentials"),
            WebsiteOperationParameters.GetBoolean(parameters, "preflight"));
    }

    public static Dictionary<string, object?> BuildBody(ProxyArguments arguments, string operate)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = arguments.WebsiteId,
            ["operate"] = operate,
            ["name"] = arguments.ProxyName,
            ["match"] = arguments.Match,
            ["proxyPass"] = arguments.ProxyPass,
            ["proxyHost"] = arguments.ProxyHost,
            ["enable"] = arguments.Enable,
            ["cache"] = arguments.Cache,
            ["cacheTime"] = arguments.CacheTime,
            ["cacheUnit"] = arguments.CacheUnit,
            ["serverCacheTime"] = arguments.ServerCacheTime,
            ["serverCacheUnit"] = arguments.ServerCacheUnit,
            ["sni"] = arguments.Sni,
            ["proxySSLName"] = arguments.ProxySslName ?? string.Empty,
            ["sslVerify"] = arguments.SslVerify,
            ["cors"] = arguments.Cors,
            ["allowOrigins"] = arguments.AllowOrigins ?? string.Empty,
            ["allowMethods"] = arguments.AllowMethods ?? string.Empty,
            ["allowHeaders"] = arguments.AllowHeaders ?? string.Empty,
            ["allowCredentials"] = arguments.AllowCredentials,
            ["preflight"] = arguments.Preflight
        };
    }

    public static JsonObject Detail(ProxyArguments arguments, string operate) => new()
    {
        ["id"] = arguments.WebsiteId,
        ["operate"] = operate,
        ["name"] = arguments.ProxyName,
        ["match"] = arguments.Match,
        ["proxyPass"] = arguments.ProxyPass,
        ["enable"] = arguments.Enable
    };
}

/// <summary>新增反向代理（<c>proxy.create</c>）：Medium；调用 <c>POST websites/proxies/update</c>（operate=create）。</summary>
public sealed class ProxyCreateHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "proxy.create";

    protected override string TitlePrefix => "新增反向代理";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) =>
        _ = ProxyArgumentReader.Read(context.Parameters);

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["新增反向代理会修改 Nginx 配置并重载；请确认路径与目标地址不会与现有代理冲突。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ProxyArgumentReader.Read(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点为网站 {target.DisplayName} 新增反向代理 {arguments.ProxyName}"
            + $"（{arguments.Match} → {arguments.ProxyPass}，启用={Flag(arguments.Enable)}）。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ProxyArgumentReader.Read(context.Parameters);
        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.ProxyUpdate, ProxyArgumentReader.BuildBody(arguments, "create"), cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"反向代理 {arguments.ProxyName} 已创建（{arguments.Match} → {arguments.ProxyPass}）。",
            ProxyArgumentReader.Detail(arguments, "create"),
            WebsiteTargetId(arguments.WebsiteId));
    }

    private static string Flag(bool value) => value ? "是" : "否";
}

/// <summary>编辑反向代理（<c>proxy.update</c>）：Medium；调用 <c>POST websites/proxies/update</c>（operate=edit）。</summary>
public sealed class ProxyUpdateHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "proxy.update";

    protected override string TitlePrefix => "编辑反向代理";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) =>
        _ = ProxyArgumentReader.Read(context.Parameters);

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["编辑反向代理会修改 Nginx 配置并重载；修改路径或目标地址可能影响正在使用的业务。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ProxyArgumentReader.Read(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点编辑网站 {target.DisplayName} 的反向代理 {arguments.ProxyName}"
            + $"（{arguments.Match} → {arguments.ProxyPass}，启用={Flag(arguments.Enable)}）。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ProxyArgumentReader.Read(context.Parameters);
        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.ProxyUpdate, ProxyArgumentReader.BuildBody(arguments, "edit"), cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"反向代理 {arguments.ProxyName} 已更新。",
            ProxyArgumentReader.Detail(arguments, "edit"),
            WebsiteTargetId(arguments.WebsiteId));
    }

    private static string Flag(bool value) => value ? "是" : "否";
}

/// <summary>删除反向代理（<c>proxy.delete</c>）：High；调用 <c>POST websites/proxies/delete</c>。</summary>
public sealed class ProxyDeleteHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "proxy.delete";

    protected override string TitlePrefix => "删除反向代理";

    protected override OperationRisk SingleNodeRisk => OperationRisk.High;

    private sealed record Arguments(long WebsiteId, string ProxyName, string? WebsiteName);

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) => _ = Read(context.Parameters);

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
    [
        "删除反向代理会移除对应 Nginx 路由，访问将中断；操作不可回滚。",
        "如需保留配置，请先在 1Panel 中备份网站配置。"
    ];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = Read(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点从网站 {target.DisplayName} 删除反向代理 {arguments.ProxyName}。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = Read(context.Parameters);

        var result = await context.Client
            .PostAsync<JsonElement>(
                WebsiteRoutes.ProxyDelete,
                new Dictionary<string, object?>
                {
                    ["id"] = arguments.WebsiteId,
                    ["name"] = arguments.ProxyName
                },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"反向代理 {arguments.ProxyName} 删除指令已提交。",
            new JsonObject { ["id"] = arguments.WebsiteId, ["name"] = arguments.ProxyName },
            WebsiteTargetId(arguments.WebsiteId));
    }

    private static Arguments Read(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var proxyName = WebsiteOperationParameters.ValidateProxyName(
            WebsiteOperationParameters.RequireString(parameters, "proxyName", "代理名称（proxyName）", 64));
        return new Arguments(target.WebsiteId, proxyName, target.Name);
    }
}

/// <summary>启停反向代理（<c>proxy.status</c>）：Medium；调用 <c>POST websites/proxies/status</c>。</summary>
public sealed class ProxyStatusHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "proxy.status";

    protected override string TitlePrefix => "启停反向代理";

    protected override OperationRisk SingleNodeRisk => OperationRisk.Medium;

    private sealed record Arguments(long WebsiteId, string ProxyName, string StatusLabel);

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) => _ = Read(context.Parameters);

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
        ["启停反向代理会修改 Nginx 配置并重载；停用后对应路径将不再转发。"];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = Read(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点把网站 {target.DisplayName} 的反向代理 {arguments.ProxyName} 设为{arguments.StatusLabel}。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = Read(context.Parameters);

        var result = await context.Client
            .PostAsync<JsonElement>(
                WebsiteRoutes.ProxyStatus,
                new Dictionary<string, object?>
                {
                    ["id"] = arguments.WebsiteId,
                    ["name"] = arguments.ProxyName,
                    ["status"] = arguments.StatusLabel == "启用" ? "enable" : "disable"
                },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"反向代理 {arguments.ProxyName} 已设为{arguments.StatusLabel}。",
            new JsonObject
            {
                ["id"] = arguments.WebsiteId,
                ["name"] = arguments.ProxyName,
                ["status"] = arguments.StatusLabel == "启用" ? "enable" : "disable"
            },
            WebsiteTargetId(arguments.WebsiteId));
    }

    private static Arguments Read(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var proxyName = WebsiteOperationParameters.ValidateProxyName(
            WebsiteOperationParameters.RequireString(parameters, "proxyName", "代理名称（proxyName）", 64));
        var status = (WebsiteOperationParameters.OptionalString(parameters, "status", 16) ?? "enable").ToLowerInvariant();
        if (status is not ("enable" or "disable"))
        {
            throw new OperationValidationException("代理状态只支持 enable 或 disable。");
        }

        return new Arguments(target.WebsiteId, proxyName, status == "enable" ? "启用" : "停用");
    }
}
