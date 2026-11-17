using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Containers;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;
using ContainerItemDto = PanelFleet.Client.Dto.Containers.ContainerItemDto;

namespace PanelFleet.Application.Containers;

/// <summary>P6 容器只读查询：列表/详情/统计/日志/Compose 列表。</summary>
public interface IContainerService
{
    Task<ContainerListResult> ListAsync(ContainerQuery query, CancellationToken cancellationToken);

    Task<ContainerDetail> GetDetailAsync(Guid nodeId, string containerName, CancellationToken cancellationToken);

    Task<ContainerStatsView> GetStatsAsync(
        Guid nodeId,
        string containerName,
        string? containerId,
        CancellationToken cancellationToken);

    Task<ContainerLogView> GetLogsAsync(Guid nodeId, ContainerLogRequest request, CancellationToken cancellationToken);

    Task<ComposeListResult> ListComposeAsync(ContainerQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// 容器只读服务：逐节点调用 1Panel 容器接口并聚合。
/// <para>只读调用不进入写操作内核；所有跨节点调用并发受限、单节点超时独立、失败按节点隔离。</para>
/// </summary>
public sealed class ContainerService : IContainerService
{
    private readonly INodeStore _nodeStore;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly IContainerLogClientFactory _logClientFactory;
    private readonly ContainerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContainerService> _logger;

    public ContainerService(
        INodeStore nodeStore,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        IContainerLogClientFactory logClientFactory,
        ContainerOptions options,
        TimeProvider timeProvider,
        ILogger<ContainerService> logger)
    {
        _nodeStore = nodeStore;
        _protector = protector;
        _clientFactory = clientFactory;
        _logClientFactory = logClientFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ContainerListResult> ListAsync(ContainerQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var state = ContainerStates.Filterable.Contains(query.State) ? query.State : "all";
        var orderBy = query.OrderBy is "name" or "createdAt" or "state" ? query.OrderBy : "name";
        var order = query.Order is "ascending" or "descending" ? query.Order : "ascending";
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var nodes = await ResolveNodesAsync(query.NodeId is { } id ? new NodeId(id) : null, cancellationToken)
            .ConfigureAwait(false);
        var perNodePageSize = Math.Max(_options.PerNodePageSize, pageSize);

        var calls = await CallEachNodeAsync(
                nodes,
                async (node, client, nodeToken) =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var search = await client
                        .PostAsync<PagedItemsDto<ContainerItemDto>>(
                            ContainerRoutes.Search,
                            new ContainerSearchRequest(1, perNodePageSize, query.Name, state, string.Empty, orderBy, order),
                            nodeToken)
                        .ConfigureAwait(false);

                    if (search.Error is not null)
                    {
                        return new ContainerNodeResult(
                            node.Id.Value,
                            node.Name,
                            ContainerNodeState.Failed,
                            0,
                            [],
                            false,
                            search.Error,
                            stopwatch.Elapsed);
                    }

                    var stats = await LoadNodeStatsAsync(client, nodeToken).ConfigureAwait(false);
                    var items = search.Data?.Items ?? [];
                    var rows = items
                        .Select(item => MapRow(node, item, stats))
                        .ToArray();
                    var total = ToInt(search.Data?.Total ?? rows.Length);

                    return new ContainerNodeResult(
                        node.Id.Value,
                        node.Name,
                        ContainerNodeState.Success,
                        total,
                        rows,
                        total > rows.Length,
                        null,
                        stopwatch.Elapsed);
                },
                cancellationToken)
            .ConfigureAwait(false);

        var listNodeResults = calls
            .Select(call => call.Value ?? new ContainerNodeResult(
                call.Node.Id.Value,
                call.Node.Name,
                ContainerNodeState.Failed,
                0,
                [],
                false,
                call.Error,
                call.Elapsed))
            .ToArray();
        var succeeded = listNodeResults.Where(result => result.State == ContainerNodeState.Success).ToArray();
        var matched = succeeded
            .SelectMany(result => result.Rows)
            .ToArray();
        matched = SortRows(matched, orderBy, order);

        var totalMatched = succeeded.Sum(result => result.Total);
        var totalPages = totalMatched == 0 ? 0 : (int)Math.Ceiling(totalMatched / (double)pageSize);
        var pagedRows = matched
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return new ContainerListResult(
            pagedRows,
            totalMatched,
            page,
            pageSize,
            totalPages,
            listNodeResults.Length,
            succeeded.Length,
            listNodeResults.Length - succeeded.Length,
            listNodeResults,
            _timeProvider.GetUtcNow());
    }

    public async Task<ContainerDetail> GetDetailAsync(
        Guid nodeId,
        string containerName,
        CancellationToken cancellationToken)
    {
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));

        var inspectTask = client.PostAsync<JsonElement>(
            ContainerRoutes.Inspect,
            new ContainerInspectRequest(containerName, "container"),
            timeoutCts.Token);
        var infoTask = client.PostAsync<JsonElement>(
            ContainerRoutes.Info,
            new { name = containerName },
            timeoutCts.Token);

        var inspect = await inspectTask.ConfigureAwait(false);
        var info = await infoTask.ConfigureAwait(false);

        if (inspect.Error is not null)
        {
            return new ContainerDetail(
                node.Id.Value,
                node.Name,
                containerName,
                null,
                new Dictionary<string, string?>(StringComparer.Ordinal),
                null,
                _timeProvider.GetUtcNow(),
                inspect.Error);
        }

        var fields = FlattenInspect(inspect.Data, info.Data);
        var containerId = fields.TryGetValue("id", out var id) ? id : null;
        var stats = await GetStatsInternalAsync(node.Id.Value, containerName, containerId, timeoutCts.Token)
            .ConfigureAwait(false);

        return new ContainerDetail(
            node.Id.Value,
            node.Name,
            fields.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name) ? name : containerName,
            containerId,
            fields,
            stats,
            _timeProvider.GetUtcNow(),
            null);
    }

    public async Task<ContainerStatsView> GetStatsAsync(
        Guid nodeId,
        string containerName,
        string? containerId,
        CancellationToken cancellationToken)
    {
        return await GetStatsInternalAsync(nodeId, containerName, containerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContainerLogView> GetLogsAsync(
        Guid nodeId,
        ContainerLogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContainerName);

        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);
        var tail = Math.Clamp(request.Tail, 1, _options.LogTailMax);
        var since = string.IsNullOrWhiteSpace(request.Since) ? "all" : request.Since.Trim();

        using var logClient = _logClientFactory.Create(BuildDescriptor(node));
        var result = await logClient
            .ReadAsync(new ContainerLogQuery(request.ContainerName, Compose: null, since, tail, request.Timestamp), cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            _logger.LogWarning(
                "容器日志读取失败 节点 {NodeId} 容器 {Container} 错误 {Kind}",
                node.Id,
                request.ContainerName,
                result.Error.Kind);
        }

        return new ContainerLogView(
            node.Id.Value,
            node.Name,
            request.ContainerName,
            since,
            tail,
            request.Timestamp,
            result.Lines,
            result.Truncated,
            _timeProvider.GetUtcNow(),
            result.Error);
    }

    public async Task<ComposeListResult> ListComposeAsync(
        ContainerQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var nodes = await ResolveNodesAsync(query.NodeId is { } id ? new NodeId(id) : null, cancellationToken)
            .ConfigureAwait(false);

        var calls = await CallEachNodeAsync(
                nodes,
                async (node, client, nodeToken) =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var search = await client
                        .PostAsync<PagedItemsDto<ComposeItemDto>>(
                            ContainerRoutes.ComposeSearch,
                            new ComposeSearchRequest(1, _options.PerNodePageSize, query.Name, false),
                            nodeToken)
                        .ConfigureAwait(false);

                    if (search.Error is not null)
                    {
                        return new ComposeNodeResult(
                            node.Id.Value,
                            node.Name,
                            ContainerNodeState.Failed,
                            0,
                            [],
                            false,
                            search.Error,
                            stopwatch.Elapsed);
                    }

                    var items = search.Data?.Items ?? [];
                    var projects = items.Select(item => MapCompose(node, item)).ToArray();
                    var total = ToInt(search.Data?.Total ?? projects.Length);
                    return new ComposeNodeResult(
                        node.Id.Value,
                        node.Name,
                        ContainerNodeState.Success,
                        total,
                        projects,
                        total > projects.Length,
                        null,
                        stopwatch.Elapsed);
                },
                cancellationToken)
            .ConfigureAwait(false);

        var composeNodeResults = calls
            .Select(call => call.Value ?? new ComposeNodeResult(
                call.Node.Id.Value,
                call.Node.Name,
                ContainerNodeState.Failed,
                0,
                [],
                false,
                call.Error,
                call.Elapsed))
            .ToArray();
        var succeeded = composeNodeResults
            .Where(result => result.State == ContainerNodeState.Success)
            .ToArray();
        var allProjects = succeeded
            .SelectMany(result => result.Projects)
            .OrderBy(project => project.NodeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var totalMatched = succeeded.Sum(result => result.Total);
        var paged = allProjects
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return new ComposeListResult(
            paged,
            totalMatched,
            composeNodeResults.Length,
            succeeded.Length,
            composeNodeResults.Length - succeeded.Length,
            composeNodeResults,
            _timeProvider.GetUtcNow());
    }

    private async Task<ContainerStatsView> GetStatsInternalAsync(
        Guid nodeId,
        string containerName,
        string? containerId,
        CancellationToken cancellationToken)
    {
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));

        var statsTask = client.GetAsync<ContainerRuntimeStatsDto>(
            ContainerRoutes.Stats(string.IsNullOrWhiteSpace(containerId) ? containerName : containerId),
            timeoutCts.Token);
        var itemStatsTask = client.PostAsync<ContainerItemStatsDto>(
            ContainerRoutes.ItemStats,
            new { name = containerName },
            timeoutCts.Token);

        var stats = await statsTask.ConfigureAwait(false);
        var itemStats = await itemStatsTask.ConfigureAwait(false);

        var shotTime = stats.Data is null || stats.Data.ShotTime == default
            ? (DateTimeOffset?)null
            : stats.Data.ShotTime;

        return new ContainerStatsView(
            node.Id.Value,
            node.Name,
            containerId ?? containerName,
            stats.Data?.CpuPercent ?? 0,
            stats.Data?.Memory ?? 0,
            0,
            0,
            stats.Data?.IoRead ?? 0,
            stats.Data?.IoWrite ?? 0,
            stats.Data?.NetworkRX ?? 0,
            stats.Data?.NetworkTX ?? 0,
            shotTime,
            itemStats.Data?.SizeRw,
            itemStats.Data?.SizeRootFs,
            stats.Error,
            itemStats.Error);
    }

    private async Task<Dictionary<string, ContainerListStatsDto>> LoadNodeStatsAsync(
        IPanelClient client,
        CancellationToken cancellationToken)
    {
        var stats = await client.GetAsync<List<ContainerListStatsDto>>(ContainerRoutes.ListStats, cancellationToken)
            .ConfigureAwait(false);
        if (stats.Data is null || stats.Data.Count == 0)
        {
            return new Dictionary<string, ContainerListStatsDto>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, ContainerListStatsDto>(StringComparer.Ordinal);
        foreach (var item in stats.Data)
        {
            if (!string.IsNullOrWhiteSpace(item.ContainerID))
            {
                map[item.ContainerID] = item;
            }
        }

        return map;
    }

    private ContainerRow MapRow(
        NodeRecord node,
        ContainerItemDto item,
        IReadOnlyDictionary<string, ContainerListStatsDto> stats)
    {
        var containerId = item.ContainerID ?? item.Name ?? "?";
        var name = item.Name ?? containerId;
        var statsItem = !string.IsNullOrWhiteSpace(item.ContainerID)
            && stats.TryGetValue(item.ContainerID, out var value)
                ? value
                : null;

        return new ContainerRow(
            node.Id.Value,
            node.Name,
            containerId,
            name,
            item.ImageName,
            item.State,
            item.RunTime ?? string.Empty,
            Clean(item.Ports),
            Clean(item.Network),
            Source(item),
            NullIfEmpty(item.AppName),
            Clean(item.Websites),
            item.IsPinned,
            NullIfEmpty(item.Description),
            statsItem is null
                ? null
                : new ContainerRowStats(
                    statsItem.CpuPercent,
                    statsItem.MemoryPercent,
                    (long)statsItem.MemoryUsage,
                    (long)statsItem.MemoryLimit));
    }

    private static ComposeProjectRow MapCompose(NodeRecord node, ComposeItemDto item) =>
        new(
            node.Id.Value,
            node.Name,
            item.Name ?? "?",
            item.Path ?? item.ConfigFile ?? string.Empty,
            NullIfEmpty(item.CreatedBy) ?? "Local",
            item.ContainerCount,
            item.RunningCount,
            item.ComposeFileExists,
            item.IsPinned,
            (item.Containers ?? []).Select(container => new ComposeContainerView(
                container.ContainerID ?? string.Empty,
                container.Name ?? string.Empty,
                container.State ?? "unknown",
                Clean(container.Ports))).ToArray(),
            item.ConfigFile,
            item.Workdir);

    private Dictionary<string, string?> FlattenInspect(JsonElement inspectData, JsonElement infoData)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);

        var inspectJson = inspectData.ValueKind == JsonValueKind.String ? inspectData.GetString() : null;
        if (!string.IsNullOrWhiteSpace(inspectJson))
        {
            try
            {
                using var document = JsonDocument.Parse(inspectJson);
                var root = document.RootElement;
                Add(fields, "id", GetString(root, "Id"));
                Add(fields, "name", TrimLeadingSlash(GetString(root, "Name")));
                Add(fields, "created", GetString(root, "Created"));
                Add(fields, "imageId", GetString(root, "Image"));
                Add(fields, "state", GetString(root, "State", "Status"));
                Add(fields, "startedAt", GetString(root, "State", "StartedAt"));
                Add(fields, "finishedAt", GetString(root, "State", "FinishedAt"));
                Add(fields, "exitCode", GetString(root, "State", "ExitCode"));
                Add(fields, "restartCount", GetString(root, "RestartCount"));
                Add(fields, "restartPolicy", GetString(root, "HostConfig", "RestartPolicy", "Name"));
                Add(fields, "networkMode", GetString(root, "HostConfig", "NetworkMode"));
                Add(fields, "privileged", GetString(root, "HostConfig", "Privileged"));
                Add(fields, "logDriver", GetString(root, "HostConfig", "LogConfig", "Type"));
                Add(fields, "hostname", GetString(root, "Config", "Hostname"));
                Add(fields, "workingDir", GetString(root, "Config", "WorkingDir"));
                Add(fields, "user", GetString(root, "Config", "User"));
                Add(fields, "cmd", JoinArray(Get(root, "Config", "Cmd")));
                Add(fields, "entrypoint", JoinArray(Get(root, "Config", "Entrypoint")));
                Add(fields, "env", JoinMaskedEnv(Get(root, "Config", "Env")));
                Add(fields, "envCount", ArrayLength(Get(root, "Config", "Env")));
                Add(fields, "mounts", JoinMounts(Get(root, "Mounts")));
                Add(fields, "networks", JoinNetworks(Get(root, "NetworkSettings", "Networks")));
                Add(fields, "ports", JoinInspectPorts(Get(root, "NetworkSettings", "Ports")));
                Add(fields, "composeProject", GetString(root, "Config", "Labels", "com.docker.compose.project"));
                Add(fields, "composeService", GetString(root, "Config", "Labels", "com.docker.compose.service"));
                Add(fields, "composeWorkdir", GetString(root, "Config", "Labels", "com.docker.compose.project.working_dir"));
            }
            catch (JsonException)
            {
                // inspect 原始串解析失败不阻断详情：仍返回 info 汇总字段。
                Add(fields, "inspectError", "inspect 原始数据不是合法 JSON。");
            }
        }

        if (infoData.ValueKind == JsonValueKind.Object)
        {
            AddIfMissing(fields, "image", GetString(infoData, "image"));
            AddIfMissing(fields, "hostname", GetString(infoData, "hostname"));
            AddIfMissing(fields, "networks", JoinNetworks(Get(infoData, "networks")));
            AddIfMissing(fields, "ports", JoinInfoPorts(Get(infoData, "ports")));
            AddIfMissing(fields, "volumes", JoinVolumes(Get(infoData, "volumes")));
            AddIfMissing(fields, "restartPolicy", GetString(infoData, "restartPolicy"));
            AddIfMissing(fields, "env", JoinMaskedEnv(Get(infoData, "env")));
            AddIfMissing(fields, "tty", GetString(infoData, "tty"));
            AddIfMissing(fields, "openStdin", GetString(infoData, "openStdin"));
            AddIfMissing(fields, "workingDir", GetString(infoData, "workingDir"));
            AddIfMissing(fields, "user", GetString(infoData, "user"));
        }

        return fields;
    }

    private async Task<IReadOnlyList<NodeRecord>> ResolveNodesAsync(NodeId? nodeId, CancellationToken cancellationToken)
    {
        if (nodeId is { } single)
        {
            return [await ResolveNodeAsync(single, cancellationToken).ConfigureAwait(false)];
        }

        var nodes = await _nodeStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return nodes.Where(node => node.IsEnabled).ToArray();
    }

    private async Task<NodeRecord> ResolveNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        var node = await _nodeStore.FindAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (node is null || !node.IsEnabled)
        {
            throw new ContainerNodeNotFoundException(nodeId.Value);
        }

        return node;
    }

    private CancellationTokenSource CreateNodeTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.NodeTimeout);
        return cts;
    }

    private async Task<IReadOnlyList<NodeCallResult<T>>> CallEachNodeAsync<T>(
        IReadOnlyList<NodeRecord> nodes,
        Func<NodeRecord, IPanelClient, CancellationToken, Task<T>> callback,
        CancellationToken cancellationToken)
    {
        var results = new NodeCallResult<T>[nodes.Count];
        if (nodes.Count == 0)
        {
            return results;
        }

        using var semaphore = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        var tasks = new Task[nodes.Count];

        async Task RunAsync(int index)
        {
            var node = nodes[index];
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = new NodeCallResult<T>(node, default, Canceled(node), stopwatch.Elapsed);
                return;
            }

            try
            {
                using var timeoutCts = CreateNodeTimeout(cancellationToken);
                using var client = _clientFactory.Create(BuildDescriptor(node));
                var value = await callback(node, client, timeoutCts.Token).ConfigureAwait(false);
                results[index] = new NodeCallResult<T>(node, value, null, stopwatch.Elapsed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results[index] = new NodeCallResult<T>(node, default, Canceled(node), stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                results[index] = new NodeCallResult<T>(
                    node,
                    default,
                    Error(node, PanelErrorKind.Timeout, $"超过 {_options.NodeTimeoutSeconds} 秒未响应"),
                    stopwatch.Elapsed);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(
                    exception,
                    "容器只读请求异常 节点 {NodeId} 类型 {Type}",
                    node.Id,
                    exception.GetType().Name);
                results[index] = new NodeCallResult<T>(
                    node,
                    default,
                    Error(node, PanelErrorKind.Protocol, Truncate(exception.Message, 200)),
                    stopwatch.Elapsed);
            }
            finally
            {
                semaphore.Release();
            }
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            var captured = index;
            tasks[captured] = RunAsync(captured);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static PanelError Canceled(NodeRecord node) =>
        Error(node, PanelErrorKind.Canceled, "请求已取消");

    private static PanelError Error(NodeRecord node, PanelErrorKind kind, string detail) =>
        new(kind, $"[{node.Name}] {detail}");

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    private static ContainerRow[] SortRows(IReadOnlyList<ContainerRow> rows, string orderBy, string order)
    {
        var descending = order == "descending";
        Func<ContainerRow, string> selector = orderBy switch
        {
            "state" => row => ContainerStates.Normalize(row.State),
            _ => row => row.Name
        };

        var comparer = StringComparer.OrdinalIgnoreCase;
        var ordered = descending
            ? rows.OrderByDescending(selector, comparer).ThenBy(row => row.NodeName, comparer)
            : rows.OrderBy(selector, comparer).ThenBy(row => row.NodeName, comparer);
        return ordered.ToArray();
    }

    private static string Source(ContainerItemDto item) => item switch
    {
        { IsFromApp: true } => "应用",
        { IsFromCompose: true } => "Compose",
        _ => "手动"
    };

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? values) =>
        values is null
            ? []
            : values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static void Add(Dictionary<string, string?> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }

    private static void AddIfMissing(Dictionary<string, string?> fields, string key, string? value)
    {
        if (!fields.ContainsKey(key))
        {
            Add(fields, key, value);
        }
    }

    private static JsonElement? Get(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(name, out var next))
            {
                return null;
            }

            element = next;
        }

        return element;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var value = Get(element, path);
        if (value is not { } elementValue)
        {
            return null;
        }

        return elementValue.ValueKind switch
        {
            JsonValueKind.String => elementValue.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => elementValue.GetRawText(),
            _ => null
        };
    }

    private static string? JoinArray(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var parts = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return parts.Length == 0 ? null : string.Join(" ", parts);
    }

    private static string? JoinMaskedEnv(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var parts = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => SecretMasker.MaskEntry(item.GetString()))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n", parts);
    }

    private static string? ArrayLength(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array
            ? array.GetArrayLength().ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? JoinMounts(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetString(item, "Type");
            var source = GetString(item, "Source");
            var destination = GetString(item, "Destination");
            var mode = GetString(item, "Mode");
            if (string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            parts.Add($"{type}: {source} → {destination}{(string.IsNullOrWhiteSpace(mode) ? string.Empty : " (" + mode + ")")}");
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string? JoinNetworks(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } networks)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var network in networks.EnumerateObject())
        {
            var ip = GetString(network.Value, "IPAddress");
            parts.Add(string.IsNullOrWhiteSpace(ip) ? network.Name : $"{network.Name}: {ip}");
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string? JoinInspectPorts(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } ports)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var port in ports.EnumerateObject())
        {
            if (port.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var binding in port.Value.EnumerateArray())
            {
                var hostIp = GetString(binding, "HostIp");
                var hostPort = GetString(binding, "HostPort");
                parts.Add(string.IsNullOrWhiteSpace(hostPort)
                    ? port.Name
                    : $"{hostIp}:{hostPort} → {port.Name}");
            }
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? JoinInfoPorts(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var parts = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item =>
            {
                var hostPort = GetString(item, "hostPort");
                var containerPort = GetString(item, "containerPort");
                var protocol = GetString(item, "protocol");
                return string.IsNullOrWhiteSpace(hostPort)
                    ? $"{containerPort}/{protocol}"
                    : $"{hostPort} → {containerPort}/{protocol}";
            })
            .ToArray();
        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    private static string? JoinVolumes(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var parts = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item =>
            {
                var source = GetString(item, "sourceDir");
                var destination = GetString(item, "containerDir");
                return string.IsNullOrWhiteSpace(destination) ? null : $"{source} → {destination}";
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n", parts);
    }

    private static string? TrimLeadingSlash(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.TrimStart('/');

    private sealed record NodeCallResult<T>(
        NodeRecord Node,
        T? Value,
        PanelError? Error,
        TimeSpan Elapsed);
}
