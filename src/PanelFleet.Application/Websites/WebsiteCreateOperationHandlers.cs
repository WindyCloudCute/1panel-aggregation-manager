using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Websites;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Websites;

/// <summary>
/// 创建网站（<c>website.create</c>）：High；调用 <c>POST websites</c>。
/// <para>建站只能针对单个节点（1Panel 建站是节点级动作），批量节点会直接拒绝；
/// 建站成功后 1Panel 在后台按 taskID 创建，页面提示稍后刷新。</para>
/// </summary>
public sealed class WebsiteCreateHandler : WebsiteOperationHandlerBase
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "static",
        "runtime",
        "proxy",
        "subsite"
    };

    public override string Kind => "website.create";

    protected override string TitlePrefix => "创建网站";

    protected override OperationRisk SingleNodeRisk => OperationRisk.High;

    /// <summary>建站不使用网站目标（网站尚未创建），由 <see cref="PlanAsync"/> 自行解析。</summary>
    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ReadArguments(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点创建{TypeLabel(arguments.Type)}网站 {arguments.PrimaryDomain}。";
    }

    public override Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Nodes.Count != 1)
        {
            throw new OperationValidationException("建站只支持单节点；请先选择目标节点再创建网站。");
        }

        var arguments = ReadArguments(context.Parameters);
        var node = context.Nodes[0];
        var warnings = new List<string>
        {
            "建站会写入 1Panel Nginx 配置并可能创建站点目录，操作不会自动回滚。",
            "请确认域名已在 DNS 中解析到目标节点；否则网站会创建成功但无法访问。",
            "创建后 1Panel 会在后台执行建站任务，列表可能需要稍后刷新才能看到新网站。"
        };

        if (arguments.Type == "runtime")
        {
            warnings.Add("运行环境建站需要目标 PHP 运行环境已创建并处于运行状态。");
        }
        else if (arguments.Type == "subsite")
        {
            warnings.Add("子网站会复用父网站的目录与运行环境，请确认父网站选择正确。");
        }
        else if (arguments.Type == "proxy")
        {
            warnings.Add("反向代理建站需要目标后端地址可访问，否则访问会返回 502。");
        }

        return Task.FromResult(new OperationPlan(
            $"创建网站 {arguments.PrimaryDomain}",
            $"在节点 {node.Name} 创建{TypeLabel(arguments.Type)}网站 {arguments.PrimaryDomain}"
                + $"（别名 {arguments.Alias}，分组 ID {arguments.GroupId.ToString(CultureInfo.InvariantCulture)}，端口 {arguments.Port.ToString(CultureInfo.InvariantCulture)}）。",
            OperationRisk.High,
            context.Nodes.Select(item => new OperationTarget(item.Id, item.Name)).ToArray(),
            warnings,
            TypedConfirmationText: $"{arguments.PrimaryDomain} @ {node.Name}",
            TargetType: "website"));
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);
        var taskId = Guid.NewGuid().ToString("N");

        var domains = arguments.Domains
            .Select(domain => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["domain"] = domain,
                ["port"] = arguments.Port,
                ["ssl"] = false
            })
            .ToArray();

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = arguments.Type,
            ["alias"] = arguments.Alias,
            ["remark"] = arguments.Remark ?? string.Empty,
            ["webSiteGroupID"] = arguments.GroupId,
            ["IPV6"] = arguments.Ipv6,
            ["domains"] = domains,
            ["appType"] = "installed",
            ["appInstallID"] = 0,
            ["runtimeID"] = arguments.RuntimeId,
            ["taskID"] = taskId,
            ["proxy"] = arguments.Proxy ?? string.Empty,
            ["siteDir"] = arguments.SiteDir ?? string.Empty,
            ["parentWebsiteID"] = arguments.ParentWebsiteId
        };

        if (arguments.Type == "runtime")
        {
            body["runtimeConfig"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["proxyType"] = "tcp",
                ["port"] = arguments.Port
            };
            body["runtimeType"] = arguments.RuntimeType;
        }

        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.Create, body, cancellationToken)
            .ConfigureAwait(false);

        var detail = new JsonObject
        {
            ["type"] = arguments.Type,
            ["primaryDomain"] = arguments.PrimaryDomain,
            ["domains"] = new JsonArray(arguments.Domains.Select(domain => (JsonNode?)JsonValue.Create(domain)).ToArray()),
            ["webSiteGroupID"] = arguments.GroupId,
            ["runtimeID"] = arguments.RuntimeId,
            ["taskID"] = taskId
        };

        return FromCall(
            context,
            result,
            $"建站请求已提交（{arguments.PrimaryDomain}）；1Panel 正在后台创建，请稍后刷新网站列表。",
            detail,
            arguments.PrimaryDomain);
    }

    internal sealed record CreateArguments(
        string Type,
        string PrimaryDomain,
        IReadOnlyList<string> Domains,
        string Alias,
        string? Remark,
        long GroupId,
        int Port,
        long RuntimeId,
        string RuntimeType,
        string? Proxy,
        long ParentWebsiteId,
        string? SiteDir,
        bool Ipv6);

    internal static CreateArguments ReadArguments(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var type = (parameters.GetString("type") ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedTypes.Contains(type))
        {
            throw new OperationValidationException("网站类型只支持 static、runtime、proxy、subsite。");
        }

        var primaryDomain = WebsiteOperationParameters.ValidateDomain(
            WebsiteOperationParameters.RequireString(parameters, "primaryDomain", "主域名（primaryDomain）", 253));

        var domains = new List<string> { primaryDomain };
        foreach (var domain in parameters.GetStringArray("otherDomains"))
        {
            if (domains.Count >= 20)
            {
                break;
            }

            var validated = WebsiteOperationParameters.ValidateDomain(domain);
            if (!domains.Contains(validated, StringComparer.OrdinalIgnoreCase))
            {
                domains.Add(validated);
            }
        }

        var alias = WebsiteOperationParameters.OptionalString(parameters, "alias", 128) ?? primaryDomain;
        var groupId = WebsiteOperationParameters.OptionalInt64(parameters, "webSiteGroupId")
            ?? WebsiteOperationParameters.OptionalInt64(parameters, "groupId")
            ?? 0;
        var port = WebsiteOperationParameters.ValidatePort(parameters.GetInt32("port") ?? 80);

        var runtimeId = 0L;
        var runtimeType = (parameters.GetString("runtimeType") ?? "php").Trim().ToLowerInvariant();
        if (type == "runtime")
        {
            runtimeId = WebsiteOperationParameters.RequireInt64(parameters, "runtimeId", "运行环境 ID（runtimeId）");
            if (runtimeType is not ("php" or "node" or "java" or "go" or "python" or "dotnet"))
            {
                throw new OperationValidationException("运行环境类型只支持 php、node、java、go、python、dotnet。");
            }
        }

        string? proxy = null;
        if (type == "proxy")
        {
            var value = WebsiteOperationParameters.RequireString(parameters, "proxy", "代理目标地址（proxy）", 512);
            proxy = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? value
                : "http://" + value;
        }

        var parentWebsiteId = type == "subsite"
            ? WebsiteOperationParameters.RequireInt64(parameters, "parentWebsiteId", "父网站 ID（parentWebsiteId）")
            : 0L;

        return new CreateArguments(
            type,
            primaryDomain,
            domains,
            alias,
            WebsiteOperationParameters.OptionalRemark(parameters),
            groupId,
            port,
            runtimeId,
            runtimeType,
            proxy,
            parentWebsiteId,
            WebsiteOperationParameters.OptionalString(parameters, "siteDir", 512),
            WebsiteOperationParameters.GetBoolean(parameters, "IPV6"));
    }

    private static string TypeLabel(string type) => type switch
    {
        "static" => "静态",
        "runtime" => "运行环境",
        "proxy" => "反向代理",
        "subsite" => "子",
        _ => type
    };
}

/// <summary>
/// 设置网站主域名（<c>domain.primary</c>）：High。
/// <para>1Panel 没有独立的设主域名接口，通过读取当前网站信息后调用 <c>POST websites/update</c> 仅修改 primaryDomain 实现。</para>
/// </summary>
public sealed class DomainPrimaryHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "domain.primary";

    protected override string TitlePrefix => "设置网站主域名";

    protected override OperationRisk SingleNodeRisk => OperationRisk.High;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) => _ = ReadArguments(context.Parameters);

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
    [
        "修改主域名会改变网站默认访问地址，已部署证书的域名匹配可能失效。",
        "请确认新域名已在 DNS 中解析到目标节点；操作不会自动回滚。"
    ];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ReadArguments(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点把网站 {target.DisplayName} 的主域名改为 {arguments.PrimaryDomain}。";
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);
        var detailResult = await context.Client
            .GetAsync<WebsiteDetailDto>(WebsiteRoutes.Detail(arguments.WebsiteId), cancellationToken)
            .ConfigureAwait(false);

        if (!detailResult.IsSuccess || detailResult.Data is null)
        {
            var error = detailResult.Error ?? new PanelError(PanelErrorKind.Protocol, "无法读取网站详情，未修改主域名。");
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                error.Message,
                null,
                null,
                error,
                TimeSpan.Zero,
                WebsiteTargetId(arguments.WebsiteId));
        }

        var current = detailResult.Data;
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = arguments.WebsiteId,
            ["primaryDomain"] = arguments.PrimaryDomain,
            ["remark"] = current.Remark ?? string.Empty,
            ["webSiteGroupId"] = current.WebSiteGroupID,
            ["expireDate"] = current.ExpireDate ?? "9999-12-31",
            ["IPV6"] = current.IPV6,
            ["favorite"] = current.Favorite
        };

        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.Update, body, cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"网站主域名已更新为 {arguments.PrimaryDomain}。",
            new JsonObject
            {
                ["id"] = arguments.WebsiteId,
                ["primaryDomain"] = arguments.PrimaryDomain
            },
            WebsiteTargetId(arguments.WebsiteId));
    }

    internal sealed record Arguments(long WebsiteId, string PrimaryDomain);

    internal static Arguments ReadArguments(OperationParameters parameters)
    {
        var target = WebsiteOperationParameters.ReadTarget(parameters);
        var primaryDomain = WebsiteOperationParameters.ValidateDomain(
            WebsiteOperationParameters.RequireString(parameters, "primaryDomain", "主域名（primaryDomain）", 253));
        return new Arguments(target.WebsiteId, primaryDomain);
    }
}

/// <summary>
/// 申请 SSL 证书（<c>ssl.request</c>）：High。
/// <para>先 <c>POST websites/ssl</c> 创建证书记录，再按 provider 调用 <c>POST websites/ssl/obtain</c> 发起签发；
/// dnsManual 只创建记录，等待用户手工添加 DNS 后另行申请。</para>
/// </summary>
public sealed class SslRequestHandler : WebsiteOperationHandlerBase
{
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "dnsManual",
        "dnsAccount"
    };

    public override string Kind => "ssl.request";

    protected override string TitlePrefix => "申请 SSL 证书";

    protected override OperationRisk SingleNodeRisk => OperationRisk.High;

    protected override string TargetType => "ssl";

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target)
    {
        var arguments = ReadArguments(context.Parameters);
        return $"在 {context.Nodes.Count} 个节点为 {arguments.PrimaryDomain} 创建并申请 SSL 证书（provider={arguments.Provider}）。";
    }

    public override Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);
        var risk = context.Nodes.Count > 1 ? OperationRisk.Critical : OperationRisk.High;
        var warnings = new List<string>
        {
            "证书申请依赖域名 DNS 正确解析与 1Panel 的 ACME 账户配置，可能耗时数分钟。",
            "若请求超时，证书申请可能仍在 1Panel 后台执行，请稍后刷新证书状态或查看 1Panel 任务日志。",
            "申请失败不会自动删除已创建的证书记录，可重新申请或删除证书。"
        };

        if (context.Nodes.Count > 1)
        {
            warnings.Add("批量申请按节点顺序执行，某节点失败后停止后续节点；已执行节点不会回滚。");
        }

        var typedConfirmation = context.Nodes.Count == 1
            ? $"{arguments.PrimaryDomain} @ {context.Nodes[0].Name}"
            : null;

        return Task.FromResult(new OperationPlan(
            $"申请 SSL 证书 {arguments.PrimaryDomain}",
            $"在 {context.Nodes.Count} 个节点为 {arguments.PrimaryDomain} 创建并申请证书（provider={arguments.Provider}，ACME 账户 #{arguments.AcmeAccountId.ToString(CultureInfo.InvariantCulture)}）。",
            risk,
            context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
            warnings,
            TypedConfirmationText: typedConfirmation,
            TargetType: "ssl"));
    }

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = ReadArguments(context.Parameters);

        var createBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["primaryDomain"] = arguments.PrimaryDomain,
            ["otherDomains"] = arguments.OtherDomains,
            ["provider"] = arguments.Provider,
            ["acmeAccountId"] = arguments.AcmeAccountId,
            ["dnsAccountId"] = arguments.DnsAccountId,
            ["description"] = arguments.Description ?? string.Empty,
            ["autoRenew"] = arguments.AutoRenew,
            ["keyType"] = arguments.KeyType,
            ["apply"] = true
        };

        var createResult = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.SslCreate, createBody, cancellationToken)
            .ConfigureAwait(false);

        if (!createResult.IsSuccess)
        {
            return FromCall(
                context,
                createResult,
                "证书创建失败。",
                new JsonObject { ["primaryDomain"] = arguments.PrimaryDomain, ["provider"] = arguments.Provider },
                arguments.PrimaryDomain);
        }

        var sslId = ReadSslId(createResult.Data);
        if (sslId <= 0)
        {
            return FromCall(
                context,
                createResult,
                "证书记录已创建，但 1Panel 未返回证书 ID，无法自动发起申请；请到 1Panel 证书页手动申请。",
                new JsonObject { ["primaryDomain"] = arguments.PrimaryDomain, ["provider"] = arguments.Provider },
                arguments.PrimaryDomain);
        }

        if (string.Equals(arguments.Provider, "dnsManual", StringComparison.OrdinalIgnoreCase))
        {
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                true,
                $"证书记录已创建（ID {sslId.ToString(CultureInfo.InvariantCulture)}）；请到 1Panel 中添加 DNS TXT 记录后手动申请。",
                null,
                new JsonObject
                {
                    ["sslId"] = sslId,
                    ["primaryDomain"] = arguments.PrimaryDomain,
                    ["provider"] = arguments.Provider,
                    ["requested"] = false
                }.ToJsonString(),
                null,
                TimeSpan.Zero,
                sslId.ToString(CultureInfo.InvariantCulture));
        }

        var obtainResult = await context.Client
            .PostAsync<JsonElement>(
                WebsiteRoutes.SslObtain,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["ID"] = sslId },
                cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            obtainResult,
            $"证书 {arguments.PrimaryDomain} 已创建并发起申请（证书 ID {sslId.ToString(CultureInfo.InvariantCulture)}）；请稍后刷新状态。",
            new JsonObject
            {
                ["sslId"] = sslId,
                ["primaryDomain"] = arguments.PrimaryDomain,
                ["provider"] = arguments.Provider,
                ["requested"] = true
            },
            sslId.ToString(CultureInfo.InvariantCulture));
    }

    internal sealed record Arguments(
        string PrimaryDomain,
        IReadOnlyList<string> OtherDomains,
        string Provider,
        long AcmeAccountId,
        long DnsAccountId,
        string? Description,
        bool AutoRenew,
        string KeyType);

    internal static Arguments ReadArguments(OperationParameters parameters)
    {
        var primaryDomain = WebsiteOperationParameters.ValidateDomain(
            WebsiteOperationParameters.RequireString(parameters, "primaryDomain", "主域名（primaryDomain）", 253));

        var provider = (parameters.GetString("provider") ?? "http").Trim();
        if (!SupportedProviders.Contains(provider))
        {
            throw new OperationValidationException("证书申请方式只支持 http、dnsManual、dnsAccount。");
        }

        provider = provider.ToLowerInvariant() switch
        {
            "dnsmanual" => "dnsManual",
            "dnsaccount" => "dnsAccount",
            _ => "http"
        };

        var acmeAccountId = WebsiteOperationParameters.RequireInt64(parameters, "acmeAccountId", "ACME 账户 ID（acmeAccountId）");
        var dnsAccountId = WebsiteOperationParameters.OptionalInt64(parameters, "dnsAccountId") ?? 0;
        if (provider == "dnsAccount" && dnsAccountId <= 0)
        {
            throw new OperationValidationException("使用 DNS 账户申请时必须提供 DNS 账户 ID（dnsAccountId）。");
        }

        var otherDomains = new List<string>();
        foreach (var domain in parameters.GetStringArray("otherDomains"))
        {
            if (otherDomains.Count >= 20)
            {
                break;
            }

            var validated = WebsiteOperationParameters.ValidateDomain(domain);
            if (!string.Equals(validated, primaryDomain, StringComparison.OrdinalIgnoreCase)
                && !otherDomains.Contains(validated, StringComparer.OrdinalIgnoreCase))
            {
                otherDomains.Add(validated);
            }
        }

        var keyType = (parameters.GetString("keyType") ?? "P256").Trim();
        if (keyType is not ("P256" or "RSA2048" or "RSA4096"))
        {
            throw new OperationValidationException("证书密钥类型只支持 P256、RSA2048、RSA4096。");
        }

        return new Arguments(
            primaryDomain,
            otherDomains,
            provider,
            acmeAccountId,
            dnsAccountId,
            WebsiteOperationParameters.OptionalString(parameters, "description", 128),
            WebsiteOperationParameters.GetBoolean(parameters, "autoRenew"),
            keyType);
    }

    private static long ReadSslId(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out var value))
        {
            return value;
        }

        return 0;
    }
}

/// <summary>
/// 关闭网站 HTTPS（<c>ssl.disable</c>）：High。
/// <para>读取当前 HTTPS 配置后以 enable=false 回写，保留访问方式与协议设置；关闭后仅 HTTP 可访问。</para>
/// </summary>
public sealed class SslDisableHandler : WebsiteOperationHandlerBase
{
    public override string Kind => "ssl.disable";

    protected override string TitlePrefix => "关闭网站 HTTPS";

    protected override OperationRisk SingleNodeRisk => OperationRisk.High;

    protected override void ValidatePlan(OperationPlanContext context, WebsiteTarget target) => _ = target;

    protected override IReadOnlyList<string> BuildWarnings(OperationPlanContext context, WebsiteTarget target) =>
    [
        "关闭 HTTPS 后网站仅能通过 HTTP 访问，可能影响安全与浏览器标记。",
        "已部署证书不会被删除，可稍后重新启用 HTTPS。"
    ];

    protected override string BuildSummary(OperationPlanContext context, WebsiteTarget target) =>
        $"在 {context.Nodes.Count} 个节点关闭网站 {target.DisplayName} 的 HTTPS。";

    public override async Task<OperationStepResult> ExecuteAsync(
        OperationNodeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = WebsiteOperationParameters.ReadTarget(context.Parameters);
        var configResult = await context.Client
            .GetAsync<WebsiteHttpsDto>(WebsiteRoutes.Https(target.WebsiteId), cancellationToken)
            .ConfigureAwait(false);

        if (!configResult.IsSuccess || configResult.Data is null)
        {
            var error = configResult.Error ?? new PanelError(PanelErrorKind.Protocol, "无法读取 HTTPS 配置，未做修改。");
            return new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                false,
                error.Message,
                null,
                null,
                error,
                TimeSpan.Zero,
                WebsiteTargetId(target.WebsiteId));
        }

        var current = configResult.Data;
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["websiteId"] = target.WebsiteId,
            ["enable"] = false,
            ["websiteSSLId"] = current.SSL?.Id ?? 0,
            ["type"] = "existed",
            ["httpConfig"] = current.HttpConfig ?? "HTTPAlso",
            ["SSLProtocol"] = current.SSLProtocol ?? ["TLSv1.2", "TLSv1.3"],
            ["algorithm"] = current.Algorithm ?? string.Empty,
            ["hsts"] = current.Hsts,
            ["hstsIncludeSubDomains"] = current.HstsIncludeSubDomains,
            ["httpsPorts"] = current.HttpsPorts ?? [],
            ["http3"] = current.Http3
        };

        var result = await context.Client
            .PostAsync<JsonElement>(WebsiteRoutes.Https(target.WebsiteId), body, cancellationToken)
            .ConfigureAwait(false);

        return FromCall(
            context,
            result,
            $"网站 HTTPS 已关闭（{target.DisplayName}）；网站现在仅通过 HTTP 访问。",
            new JsonObject { ["websiteId"] = target.WebsiteId, ["enable"] = false },
            WebsiteTargetId(target.WebsiteId));
    }
}
