using System.Diagnostics;
using System.Text.Json;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Resources;

/// <summary>网站列表/详情。</summary>
public sealed class WebsiteResourceLoader : ResourceLoaderBase
{
    public override ResourceKind Kind => ResourceKind.Websites;

    public override async Task<NodeResourceResult> LoadAsync(
        IPanelClient client,
        NodeRecord node,
        ResourceOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var rows = new List<ResourceRow>();
        var total = 0;
        var truncated = false;

        // 分类使用 1Panel 自定义分组（groups/search，type=website）；网站列表接口本身不带分组字段，
        // 因此按分组逐个查询（分组数通常很小），既拿到分组归属又保持每节点条数上限。
        var groupsResult = await client
            .PostAsync<List<GroupItemDto>>(ResourceRoutes.GroupsSearch, new { }, cancellationToken)
            .ConfigureAwait(false);

        if (groupsResult.Error is not null)
        {
            warnings.Add($"无法读取网站分组（{groupsResult.Error.Message}），已按「未分组」展示。");
        }

        var websiteGroups = (groupsResult.Data ?? [])
            .Where(group => string.Equals(group.Type, "website", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (websiteGroups.Length == 0)
        {
            var result = await SearchAsync<WebsiteItemDto>(
                client,
                ResourceRoutes.WebsitesSearch,
                new { page = 1, pageSize = options.PerNodeLimit, orderBy = "created_at", order = "descending" },
                new { page = 1, pageSize = options.PerNodeLimit, orderBy = "primary_domain", order = "descending" },
                cancellationToken).ConfigureAwait(false);

            if (result.Error is not null)
            {
                return Failed(node, Kind, result.Error, DateTimeOffset.UtcNow, stopwatch);
            }

            var items = result.Data?.Items ?? [];
            total = ToInt(result.Data?.Total ?? items.Count);
            rows.AddRange(items
                .Take(options.PerNodeLimit)
                .Select(item => WebsiteRow(node, item, groupName: null)));
        }
        else
        {
            foreach (var group in websiteGroups)
            {
                var groupName = ResolveGroupName(group);
                var result = await SearchAsync<WebsiteItemDto>(
                    client,
                    ResourceRoutes.WebsitesSearch,
                    new
                    {
                        page = 1,
                        pageSize = options.PerNodeLimit,
                        orderBy = "created_at",
                        order = "descending",
                        websiteGroupId = group.Id
                    },
                    new
                    {
                        page = 1,
                        pageSize = options.PerNodeLimit,
                        orderBy = "primary_domain",
                        order = "descending",
                        websiteGroupId = group.Id
                    },
                    cancellationToken).ConfigureAwait(false);

                if (result.Error is not null)
                {
                    warnings.Add($"{groupName} 分组网站列表失败：{result.Error.Message}");
                    continue;
                }

                var items = result.Data?.Items ?? [];
                total += ToInt(result.Data?.Total ?? items.Count);
                rows.AddRange(items
                    .Take(options.PerNodeLimit)
                    .Select(item => WebsiteRow(node, item, groupName)));

                if (rows.Count >= options.PerNodeLimit)
                {
                    truncated = true;
                    break;
                }
            }
        }

        if (rows.Count > options.PerNodeLimit)
        {
            truncated = true;
            rows = rows.Take(options.PerNodeLimit).ToList();
        }

        return Success(
            node,
            Kind,
            rows,
            total,
            truncated || total > rows.Count,
            warnings.Count > 0 ? warnings : null,
            DateTimeOffset.UtcNow,
            stopwatch);
    }

    private ResourceRow WebsiteRow(NodeRecord node, WebsiteItemDto item, string? groupName) => Row(
        node,
        Kind,
        item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        item.PrimaryDomain ?? item.Alias ?? $"#{item.Id}",
        item.Status,
        Fields(
            ("groupName", groupName),
            ("primaryDomain", item.PrimaryDomain),
            ("alias", item.Alias),
            ("type", item.Type),
            ("expireDate", item.ExpireDate),
            ("sitePath", item.SitePath),
            ("appName", item.AppName),
            ("runtimeName", item.RuntimeName),
            ("protocol", item.Protocol),
            ("remark", item.Remark)));

    private static string ResolveGroupName(GroupItemDto group) =>
        group.IsDefault
            ? "默认分组"
            : string.IsNullOrWhiteSpace(group.Name) ? "未分组" : group.Name.Trim();

    public override async Task<ResourceDetail> LoadDetailAsync(
        IPanelClient client,
        NodeRecord node,
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(sourceId, out var id))
        {
            return DetailError(node, sourceId, new PanelError(PanelErrorKind.Validation, "网站 id 非法。"));
        }

        var result = await client.GetAsync<JsonElement>(ResourceRoutes.WebsiteDetail(id), cancellationToken)
            .ConfigureAwait(false);
        return result.Error is not null
            ? DetailError(node, sourceId, result.Error)
            : DetailOk(node, sourceId, result.Data);
    }

    private ResourceDetail DetailOk(NodeRecord node, string sourceId, JsonElement element)
    {
        var fields = ResourceFieldMapper.Flatten(element);
        var name = fields.TryGetValue("primaryDomain", out var domain) && !string.IsNullOrWhiteSpace(domain)
            ? domain
            : sourceId;
        return new ResourceDetail(node.Id, node.Name, Kind, sourceId, name ?? sourceId, fields, DateTimeOffset.UtcNow, null);
    }

    private ResourceDetail DetailError(NodeRecord node, string sourceId, PanelError error) =>
        new(node.Id, node.Name, Kind, sourceId, sourceId, new Dictionary<string, string?>(), DateTimeOffset.UtcNow, error);
}

/// <summary>已安装应用列表/详情。</summary>
public sealed class AppResourceLoader : ResourceLoaderBase
{
    public override ResourceKind Kind => ResourceKind.Apps;

    public override async Task<NodeResourceResult> LoadAsync(
        IPanelClient client,
        NodeRecord node,
        ResourceOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await SearchAsync<AppInstalledItemDto>(
            client,
            ResourceRoutes.AppsInstalledSearch,
            new { page = 1, pageSize = options.PerNodeLimit },
            fallbackBody: null,
            cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            return Failed(node, Kind, result.Error, DateTimeOffset.UtcNow, stopwatch);
        }

        var items = result.Data?.Items ?? [];
        var total = ToInt(result.Data?.Total ?? items.Count);
        var rows = items
            .Take(options.PerNodeLimit)
            .Select(item => Row(
                node,
                Kind,
                item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.Name ?? item.AppKey ?? $"#{item.Id}",
                item.Status,
                Fields(
                    ("version", item.Version),
                    ("appKey", item.AppKey),
                    ("appType", item.AppType),
                    ("httpPort", item.HttpPort?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("httpsPort", item.HttpsPort?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("path", item.Path),
                    ("message", item.Message))))
            .ToArray();

        return Success(node, Kind, rows, total, total > rows.Length, null, DateTimeOffset.UtcNow, stopwatch);
    }

    public override async Task<ResourceDetail> LoadDetailAsync(
        IPanelClient client,
        NodeRecord node,
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(sourceId, out var id))
        {
            return DetailError(node, sourceId, new PanelError(PanelErrorKind.Validation, "应用 id 非法。"));
        }

        var result = await client.GetAsync<JsonElement>(ResourceRoutes.AppDetail(id), cancellationToken)
            .ConfigureAwait(false);
        return result.Error is not null
            ? DetailError(node, sourceId, result.Error)
            : DetailOk(node, sourceId, result.Data);
    }

    private ResourceDetail DetailOk(NodeRecord node, string sourceId, JsonElement element)
    {
        var fields = ResourceFieldMapper.Flatten(element);
        var name = fields.TryGetValue("name", out var appName) && !string.IsNullOrWhiteSpace(appName)
            ? appName
            : sourceId;
        return new ResourceDetail(node.Id, node.Name, Kind, sourceId, name ?? sourceId, fields, DateTimeOffset.UtcNow, null);
    }

    private ResourceDetail DetailError(NodeRecord node, string sourceId, PanelError error) =>
        new(node.Id, node.Name, Kind, sourceId, sourceId, new Dictionary<string, string?>(), DateTimeOffset.UtcNow, error);
}
