using System.Diagnostics;
using System.Text.Json;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Resources;

/// <summary>容器列表/详情。</summary>
public sealed class ContainerResourceLoader : ResourceLoaderBase
{
    public override ResourceKind Kind => ResourceKind.Containers;

    public override async Task<NodeResourceResult> LoadAsync(
        IPanelClient client,
        NodeRecord node,
        ResourceOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await SearchAsync<ContainerItemDto>(
            client,
            ResourceRoutes.ContainersSearch,
            new
            {
                page = 1,
                pageSize = options.PerNodeLimit,
                name = string.Empty,
                state = "all",
                filters = string.Empty,
                orderBy = "name",
                order = "descending"
            },
            new { page = 1, pageSize = options.PerNodeLimit, state = "all", orderBy = "createdAt", order = "descending" },
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
                item.ContainerID ?? item.Name ?? "?",
                item.Name ?? item.ContainerID ?? "?",
                item.State,
                Fields(
                    ("image", item.ImageName),
                    ("runTime", item.RunTime),
                    ("ports", Join(item.Ports)),
                    ("source", Source(item)),
                    ("appName", item.AppName),
                    ("description", item.Description))))
            .ToArray();

        return Success(node, Kind, rows, total, total > rows.Length, null, DateTimeOffset.UtcNow, stopwatch);
    }

    public override async Task<ResourceDetail> LoadDetailAsync(
        IPanelClient client,
        NodeRecord node,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var result = await client
            .PostAsync<JsonElement>(ResourceRoutes.ContainerInfo, new { name = sourceId }, cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new ResourceDetail(
                node.Id,
                node.Name,
                Kind,
                sourceId,
                sourceId,
                new Dictionary<string, string?>(),
                DateTimeOffset.UtcNow,
                result.Error);
        }

        var fields = ResourceFieldMapper.Flatten(result.Data);
        var name = fields.TryGetValue("name", out var containerName) && !string.IsNullOrWhiteSpace(containerName)
            ? containerName
            : sourceId;
        return new ResourceDetail(node.Id, node.Name, Kind, sourceId, name ?? sourceId, fields, DateTimeOffset.UtcNow, null);
    }

    private static string? Source(ContainerItemDto item) => item switch
    {
        { IsFromApp: true } => "应用",
        { IsFromCompose: true } => "Compose",
        _ => "手动"
    };
}

/// <summary>计划任务列表/详情。</summary>
public sealed class CronjobResourceLoader : ResourceLoaderBase
{
    public override ResourceKind Kind => ResourceKind.Cronjobs;

    public override async Task<NodeResourceResult> LoadAsync(
        IPanelClient client,
        NodeRecord node,
        ResourceOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        // 分类使用 1Panel 自定义分组（groups/search，type=cronjob）；计划任务列表项自带 groupID，一次调用即可映射。
        var groupsResult = await client
            .PostAsync<List<GroupItemDto>>(ResourceRoutes.GroupsSearch, new { }, cancellationToken)
            .ConfigureAwait(false);

        if (groupsResult.Error is not null)
        {
            warnings.Add($"无法读取计划任务分组（{groupsResult.Error.Message}），已按「未分组」展示。");
        }

        var cronjobGroups = (groupsResult.Data ?? [])
            .Where(group => string.Equals(group.Type, "cronjob", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var defaultGroup = cronjobGroups.FirstOrDefault(group => group.IsDefault);
        var groupNames = cronjobGroups.ToDictionary(group => group.Id, ResolveGroupName);

        var result = await SearchAsync<CronjobItemDto>(
            client,
            ResourceRoutes.CronjobsSearch,
            new { page = 1, pageSize = options.PerNodeLimit, orderBy = "name", order = "descending" },
            new { page = 1, pageSize = options.PerNodeLimit, orderBy = "createdAt", order = "descending" },
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
                item.Name ?? $"#{item.Id}",
                item.Status,
                Fields(
                    ("groupName", ResolveCronjobGroupName(item.GroupID, groupNames, defaultGroup)),
                    ("type", item.Type),
                    ("spec", item.Spec ?? (item.Specs is { Count: > 0 } ? string.Join(", ", item.Specs) : null)),
                    ("executor", item.Executor),
                    ("command", item.Command ?? item.Script),
                    ("website", item.Website),
                    ("appID", item.AppID),
                    ("dbName", item.DbName),
                    ("retainCopies", item.RetainCopies?.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
            .ToArray();

        return Success(
            node,
            Kind,
            rows,
            total,
            total > rows.Length,
            warnings.Count > 0 ? warnings : null,
            DateTimeOffset.UtcNow,
            stopwatch);
    }

    private static string ResolveGroupName(GroupItemDto group) =>
        group.IsDefault
            ? "默认分组"
            : string.IsNullOrWhiteSpace(group.Name) ? "未分组" : group.Name.Trim();

    private static string ResolveCronjobGroupName(
        long? groupId,
        IReadOnlyDictionary<long, string> groupNames,
        GroupItemDto? defaultGroup)
    {
        if (groupId is null or 0)
        {
            return defaultGroup is null ? "默认分组" : ResolveGroupName(defaultGroup);
        }

        return groupNames.TryGetValue(groupId.Value, out var name) ? name : "未分组";
    }

    public override async Task<ResourceDetail> LoadDetailAsync(
        IPanelClient client,
        NodeRecord node,
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(sourceId, out var id))
        {
            return new ResourceDetail(
                node.Id,
                node.Name,
                Kind,
                sourceId,
                sourceId,
                new Dictionary<string, string?>(),
                DateTimeOffset.UtcNow,
                new PanelError(PanelErrorKind.Validation, "计划任务 id 非法。"));
        }

        var result = await client
            .PostAsync<JsonElement>(ResourceRoutes.CronjobInfo, new { id }, cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new ResourceDetail(
                node.Id,
                node.Name,
                Kind,
                sourceId,
                sourceId,
                new Dictionary<string, string?>(),
                DateTimeOffset.UtcNow,
                result.Error);
        }

        var fields = ResourceFieldMapper.Flatten(result.Data);
        var name = fields.TryGetValue("name", out var jobName) && !string.IsNullOrWhiteSpace(jobName)
            ? jobName
            : sourceId;
        return new ResourceDetail(node.Id, node.Name, Kind, sourceId, name ?? sourceId, fields, DateTimeOffset.UtcNow, null);
    }
}
