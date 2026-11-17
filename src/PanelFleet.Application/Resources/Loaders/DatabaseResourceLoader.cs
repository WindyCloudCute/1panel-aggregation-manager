using System.Diagnostics;
using System.Text.Json;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Resources;

/// <summary>
/// 数据库列表/详情：实例列表（MySQL/PostgreSQL/MongoDB）→ 每实例库列表。
/// </summary>
public sealed class DatabaseResourceLoader : ResourceLoaderBase
{
    private static readonly (string Engine, string Display)[] Engines =
    [
        ("mysql", "MySQL"),
        ("postgresql", "PostgreSQL"),
        ("mongodb", "MongoDB")
    ];

    public override ResourceKind Kind => ResourceKind.Databases;

    public override async Task<NodeResourceResult> LoadAsync(
        IPanelClient client,
        NodeRecord node,
        ResourceOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = new List<ResourceRow>();
        var warnings = new List<string>();
        var total = 0;
        var truncated = false;

        foreach (var (engine, display) in Engines)
        {
            var instancesResult = await client
                .GetAsync<List<DatabaseInstanceDto>>(ResourceRoutes.DatabaseInstances(engine), cancellationToken)
                .ConfigureAwait(false);

            if (instancesResult.Error is not null)
            {
                if (instancesResult.Error.Kind is PanelErrorKind.NotFound or PanelErrorKind.Unsupported)
                {
                    continue;
                }

                warnings.Add($"{display} 实例列表失败：{instancesResult.Error.Message}");
                continue;
            }

            var instances = instancesResult.Data ?? [];
            foreach (var instanceName in instances
                         .Select(instance => instance.Database)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var searchResult = await SearchAsync<DatabaseItemDto>(
                    client,
                    ResourceRoutes.DatabaseSearch(engine),
                    new
                    {
                        page = 1,
                        pageSize = options.PerNodeLimit,
                        database = instanceName,
                        orderBy = "name",
                        order = "descending"
                    },
                    new
                    {
                        page = 1,
                        pageSize = options.PerNodeLimit,
                        database = instanceName,
                        orderBy = "createdAt",
                        order = "descending"
                    },
                    cancellationToken).ConfigureAwait(false);

                if (searchResult.Error is not null)
                {
                    warnings.Add($"{display}/{instanceName} 库列表失败：{searchResult.Error.Message}");
                    continue;
                }

                var items = searchResult.Data?.Items ?? [];
                total += ToInt(searchResult.Data?.Total ?? items.Count);

                foreach (var item in items.Take(options.PerNodeLimit))
                {
                    rows.Add(Row(
                        node,
                        Kind,
                        item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        item.Name ?? $"#{item.Id}",
                        null,
                        Fields(
                            ("engine", display),
                            ("instance", instanceName),
                            ("format", item.Format),
                            ("collation", item.Collation),
                            ("from", item.From),
                            ("description", item.Description))));
                }
            }
        }

        if (rows.Count > options.PerNodeLimit)
        {
            truncated = true;
            rows = rows.Take(options.PerNodeLimit).ToList();
        }

        if (rows.Count == 0 && warnings.Count > 0)
        {
            return Failed(
                node,
                Kind,
                new PanelError(PanelErrorKind.PanelServerError, string.Join("；", warnings)),
                DateTimeOffset.UtcNow,
                stopwatch);
        }

        truncated = truncated || total > rows.Count;
        return Success(node, Kind, rows, total, truncated, warnings, DateTimeOffset.UtcNow, stopwatch);
    }

    public override async Task<ResourceDetail> LoadDetailAsync(
        IPanelClient client,
        NodeRecord node,
        string sourceId,
        CancellationToken cancellationToken)
    {
        // 数据库无独立详情接口：从列表结果中找对应行。
        var result = await LoadAsync(client, node, new ResourceOptions { PerNodeLimit = 1000 }, cancellationToken)
            .ConfigureAwait(false);
        var row = result.Rows.FirstOrDefault(item =>
            string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, sourceId, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            return new ResourceDetail(
                node.Id,
                node.Name,
                Kind,
                sourceId,
                sourceId,
                new Dictionary<string, string?>(),
                DateTimeOffset.UtcNow,
                result.Error ?? new PanelError(PanelErrorKind.NotFound, "数据库不存在或已被删除。"));
        }

        var fields = new Dictionary<string, string?>(row.Attributes, StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = row.Name
        };
        return new ResourceDetail(node.Id, node.Name, Kind, row.SourceId, row.Name, fields, DateTimeOffset.UtcNow, null);
    }
}
