using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Resources;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Cronjobs;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Cronjobs;

/// <summary>P9 计划任务只读查询：列表/详情/分组/执行记录/记录日志/周期预览。</summary>
public interface ICronjobService
{
    Task<CronjobListResult> ListAsync(CronjobQuery query, CancellationToken cancellationToken);

    Task<CronjobDetail> GetDetailAsync(Guid nodeId, long id, CancellationToken cancellationToken);

    Task<CronjobRecordsResult> GetRecordsAsync(
        Guid nodeId,
        long cronjobId,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<CronjobRecordLog> GetRecordLogAsync(Guid nodeId, long recordId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CronjobGroupView>> ListGroupsAsync(Guid? nodeId, CancellationToken cancellationToken);

    Task<CronjobSpecPreview> PreviewSpecAsync(Guid? nodeId, string spec, CancellationToken cancellationToken);
}

/// <summary>
/// 计划任务只读服务：逐节点调用 1Panel 接口并聚合；只读不进入写操作内核。
/// 节点失败隔离、单节点超时独立，与 P4/P6 只读模块一致。
/// </summary>
public sealed class CronjobService : ICronjobService
{
    private static readonly string[] OrderByFields = ["name", "status", "createdAt"];
    private static readonly string[] OrderValues = ["null", "ascending", "descending"];

    private readonly INodeStore _nodeStore;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly CronjobOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CronjobService> _logger;

    public CronjobService(
        INodeStore nodeStore,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        CronjobOptions options,
        TimeProvider timeProvider,
        ILogger<CronjobService> logger)
    {
        _nodeStore = nodeStore;
        _protector = protector;
        _clientFactory = clientFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CronjobListResult> ListAsync(CronjobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var orderBy = OrderByFields.Contains(query.OrderBy) ? query.OrderBy : "name";
        var order = OrderValues.Contains(query.Order) ? query.Order : "null";
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var groupIds = query.GroupIds.Where(id => id > 0).Distinct().ToArray();

        var nodes = await ResolveNodesAsync(query.NodeId is { } single ? new NodeId(single) : null, cancellationToken)
            .ConfigureAwait(false);

        var calls = await CallEachNodeAsync(
                nodes,
                async (node, client, nodeToken) =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var groups = await LoadCronjobGroupsAsync(client, nodeToken).ConfigureAwait(false);
                    var search = await client
                        .PostAsync<PagedItemsDto<CronjobInfoDto>>(
                            CronjobRoutes.Search,
                            new CronjobSearchRequest(
                                1,
                                _options.PerNodePageSize,
                                query.Info ?? string.Empty,
                                groupIds,
                                orderBy,
                                order),
                            nodeToken)
                        .ConfigureAwait(false);

                    if (search.Error is not null)
                    {
                        return new CronjobNodeResult(
                            node.Id.Value,
                            node.Name,
                            CronjobNodeState.Failed,
                            0,
                            [],
                            false,
                            search.Error,
                            stopwatch.Elapsed);
                    }

                    var items = search.Data?.Items ?? [];
                    var total = ToInt(search.Data?.Total ?? items.Count);
                    var rows = items
                        .Take(_options.PerNodePageSize)
                        .Select(item => MapRow(node, item, groups))
                        .ToArray();

                    return new CronjobNodeResult(
                        node.Id.Value,
                        node.Name,
                        CronjobNodeState.Success,
                        total,
                        rows,
                        total > rows.Length,
                        null,
                        stopwatch.Elapsed);
                },
                cancellationToken)
            .ConfigureAwait(false);

        var nodeResults = calls
            .Select(call => call.Value ?? new CronjobNodeResult(
                call.Node.Id.Value,
                call.Node.Name,
                CronjobNodeState.Failed,
                0,
                [],
                false,
                call.Error,
                call.Elapsed))
            .ToArray();

        var succeeded = nodeResults.Where(result => result.State == CronjobNodeState.Success).ToArray();
        var matched = succeeded.SelectMany(result => result.Rows).ToList();

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            matched = matched
                .Where(row => string.Equals(row.Status, query.Status.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var sorted = SortRows(matched, orderBy, order);
        var totalMatched = sorted.Length;
        var totalPages = totalMatched == 0 ? 0 : (int)Math.Ceiling(totalMatched / (double)pageSize);
        var pagedRows = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return new CronjobListResult(
            pagedRows,
            totalMatched,
            page,
            pageSize,
            totalPages,
            nodeResults.Length,
            succeeded.Length,
            nodeResults.Length - succeeded.Length,
            nodeResults,
            _timeProvider.GetUtcNow());
    }

    public async Task<CronjobDetail> GetDetailAsync(Guid nodeId, long id, CancellationToken cancellationToken)
    {
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));

        var result = await client
            .PostAsync<JsonElement>(CronjobRoutes.Info, new CronjobIdRequest(id), timeoutCts.Token)
            .ConfigureAwait(false);

        var fetchedAt = _timeProvider.GetUtcNow();
        if (result.Error is not null)
        {
            if (IsRecordNotFound(result.Error))
            {
                throw new CronjobNotFoundException(nodeId, id);
            }

            return new CronjobDetail(
                nodeId,
                node.Name,
                id,
                $"#{id}",
                string.Empty,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                null,
                false,
                fetchedAt,
                result.Error);
        }

        var data = result.Data;
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new CronjobNotFoundException(nodeId, id);
        }

        var name = GetString(data, "name") ?? $"#{id}";
        var type = GetString(data, "type") ?? string.Empty;
        var secretSet = !string.IsNullOrEmpty(GetString(data, "secret"));

        var fields = new Dictionary<string, string?>(
            ResourceFieldMapper.Flatten(data),
            StringComparer.OrdinalIgnoreCase);
        fields.Remove("secret");
        if (fields.TryGetValue("script", out var script))
        {
            fields["script"] = ResourceFieldMapper.Truncate(script, 2000);
        }

        var editable = new CronjobFormModel(
            id,
            name,
            type,
            GetLong(data, "groupID"),
            GetBool(data, "specCustom"),
            GetString(data, "spec") ?? string.Empty,
            GetString(data, "executor"),
            GetString(data, "scriptMode"),
            GetString(data, "script"),
            GetString(data, "command"),
            GetString(data, "containerName"),
            GetString(data, "user"),
            GetLong(data, "scriptID"),
            GetString(data, "appID"),
            GetString(data, "website"),
            GetString(data, "exclusionRules"),
            GetString(data, "dbType"),
            GetString(data, "dbName"),
            GetString(data, "url"),
            GetBool(data, "isDir"),
            GetString(data, "sourceDir"),
            (int)GetLong(data, "retainCopies"),
            (int)GetLong(data, "retryTimes"),
            GetLong(data, "timeout"),
            GetBool(data, "ignoreErr"),
            GetString(data, "args"));

        return new CronjobDetail(
            nodeId,
            node.Name,
            id,
            name,
            type,
            fields,
            editable,
            secretSet,
            fetchedAt,
            null);
    }

    public async Task<CronjobRecordsResult> GetRecordsAsync(
        Guid nodeId,
        long cronjobId,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 200);
        var endTime = end ?? _timeProvider.GetUtcNow();
        var startTime = start ?? endTime.AddDays(-_options.RecordDefaultDays);
        if (startTime > endTime)
        {
            (startTime, endTime) = (endTime, startTime);
        }

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));

        var result = await client
            .PostAsync<PagedItemsDto<CronjobRecordDto>>(
                CronjobRoutes.RecordsSearch,
                new CronjobRecordSearchRequest(
                    normalizedPage,
                    normalizedPageSize,
                    cronjobId,
                    FormatTime(startTime),
                    FormatTime(endTime),
                    status ?? string.Empty),
                timeoutCts.Token)
            .ConfigureAwait(false);

        var fetchedAt = _timeProvider.GetUtcNow();
        if (result.Error is not null)
        {
            return new CronjobRecordsResult(
                nodeId,
                node.Name,
                cronjobId,
                0,
                normalizedPage,
                normalizedPageSize,
                0,
                [],
                fetchedAt,
                result.Error);
        }

        var items = result.Data?.Items ?? [];
        var total = ToInt(result.Data?.Total ?? items.Count);
        var rows = items
            .Select(item => new CronjobRecordRow(
                item.Id,
                item.Status,
                CronjobRecordStatus.Label(item.Status),
                item.StartTime,
                item.Interval,
                string.IsNullOrWhiteSpace(item.TaskID) ? null : item.TaskID,
                item.Message,
                item.TargetPath))
            .ToArray();

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)normalizedPageSize);
        return new CronjobRecordsResult(
            nodeId,
            node.Name,
            cronjobId,
            total,
            normalizedPage,
            normalizedPageSize,
            totalPages,
            rows,
            fetchedAt,
            null);
    }

    public async Task<CronjobRecordLog> GetRecordLogAsync(Guid nodeId, long recordId, CancellationToken cancellationToken)
    {
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));

        var result = await client
            .PostAsync<string>(CronjobRoutes.RecordLog, new CronjobIdRequest(recordId), timeoutCts.Token)
            .ConfigureAwait(false);

        var fetchedAt = _timeProvider.GetUtcNow();
        if (result.Error is not null)
        {
            return new CronjobRecordLog(nodeId, recordId, string.Empty, false, fetchedAt, result.Error);
        }

        var content = result.Data ?? string.Empty;
        var truncated = false;
        if (Encoding.UTF8.GetByteCount(content) > _options.MaxRecordLogBytes)
        {
            var maxChars = Math.Max(1, _options.MaxRecordLogBytes);
            content = content.Length > maxChars ? content[..maxChars] : content;
            truncated = true;
        }

        return new CronjobRecordLog(nodeId, recordId, content, truncated, fetchedAt, null);
    }

    public async Task<IReadOnlyList<CronjobGroupView>> ListGroupsAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var nodes = await ResolveNodesAsync(nodeId is { } single ? new NodeId(single) : null, cancellationToken)
            .ConfigureAwait(false);

        var merged = new Dictionary<(long Id, string? Name, bool IsDefault), HashSet<Guid>>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutCts = CreateNodeTimeout(cancellationToken);
            using var client = _clientFactory.Create(BuildDescriptor(node));
            var groups = await LoadCronjobGroupsAsync(client, timeoutCts.Token).ConfigureAwait(false);
            foreach (var group in groups)
            {
                var key = (group.Id, group.Name, group.IsDefault);
                if (!merged.TryGetValue(key, out var nodeIds))
                {
                    nodeIds = [];
                    merged[key] = nodeIds;
                }

                nodeIds.Add(node.Id.Value);
            }
        }

        return merged
            .OrderByDescending(pair => pair.Key.IsDefault)
            .ThenBy(pair => pair.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new CronjobGroupView(
                pair.Key.Id,
                pair.Key.Name ?? "未分组",
                pair.Key.IsDefault,
                pair.Value.OrderBy(id => id).ToArray()))
            .ToArray();
    }

    public async Task<CronjobSpecPreview> PreviewSpecAsync(Guid? nodeId, string spec, CancellationToken cancellationToken)
    {
        var fetchedAt = _timeProvider.GetUtcNow();
        if (!CronjobSpec.IsValid(spec))
        {
            return new CronjobSpecPreview(
                [],
                fetchedAt,
                new PanelError(PanelErrorKind.Validation, "周期表达式不合法（支持 5 段 cron 或 @every Nm/Ns）。"));
        }

        var nodes = await ResolveNodesAsync(nodeId is { } single ? new NodeId(single) : null, cancellationToken)
            .ConfigureAwait(false);
        var node = nodes.FirstOrDefault();
        if (node is null)
        {
            return new CronjobSpecPreview([], fetchedAt, new PanelError(PanelErrorKind.Unsupported, "没有可用节点。"));
        }

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));
        var result = await client
            .PostAsync<List<string>>(CronjobRoutes.Next, new CronjobSpecRequest(spec.Trim()), timeoutCts.Token)
            .ConfigureAwait(false);

        return result.Error is not null
            ? new CronjobSpecPreview([], fetchedAt, result.Error)
            : new CronjobSpecPreview(result.Data ?? [], fetchedAt, null);
    }

    private async Task<List<GroupItemDto>> LoadCronjobGroupsAsync(IPanelClient client, CancellationToken cancellationToken)
    {
        var result = await client
            .PostAsync<List<GroupItemDto>>(CronjobRoutes.GroupsSearch, new { }, cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            _logger.LogDebug("读取计划任务分组失败：{Kind}", result.Error.Kind);
            return [];
        }

        return (result.Data ?? [])
            .Where(group => string.Equals(group.Type, "cronjob", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static CronjobRow MapRow(NodeRecord node, CronjobInfoDto item, IReadOnlyList<GroupItemDto> groups)
    {
        var type = item.Type ?? string.Empty;
        var groupName = ResolveGroupName(item.GroupID, groups);
        return new CronjobRow(
            node.Id.Value,
            node.Name,
            item.Id,
            string.IsNullOrWhiteSpace(item.Name) ? $"#{item.Id}" : item.Name.Trim(),
            type,
            CronjobTypes.Label(type),
            item.GroupID,
            groupName,
            item.Spec,
            CronjobSpec.Describe(item.Spec),
            item.SpecCustom,
            item.Status,
            item.LastRecordStatus,
            item.LastRecordTime,
            item.IsExecuting,
            BuildSourceText(item, type),
            item.RetainCopies,
            item.RetryTimes,
            item.Timeout,
            item.IgnoreErr);
    }

    private static string BuildSourceText(CronjobInfoDto item, string type) => type switch
    {
        CronjobTypes.Website => item.Website ?? string.Empty,
        CronjobTypes.App => item.AppID ?? string.Empty,
        CronjobTypes.Database => string.IsNullOrWhiteSpace(item.DBType)
            ? item.DBName ?? string.Empty
            : $"{item.DBType} / {item.DBName}",
        CronjobTypes.Directory => item.SourceDir ?? string.Empty,
        CronjobTypes.Curl => item.URL ?? string.Empty,
        CronjobTypes.Shell => string.IsNullOrWhiteSpace(item.ContainerName)
            ? item.Executor ?? "bash"
            : $"容器 {item.ContainerName}",
        _ => item.Args ?? string.Empty
    };

    private static string ResolveGroupName(long groupId, IReadOnlyList<GroupItemDto> groups)
    {
        var group = groups.FirstOrDefault(item => item.Id == groupId)
            ?? (groupId == 0 ? groups.FirstOrDefault(item => item.IsDefault) : null);
        if (group is null)
        {
            return groupId == 0 ? "默认分组" : "未分组";
        }

        return group.IsDefault ? "默认分组" : string.IsNullOrWhiteSpace(group.Name) ? "未分组" : group.Name.Trim();
    }

    private static CronjobRow[] SortRows(List<CronjobRow> rows, string orderBy, string order)
    {
        var descending = string.Equals(order, "descending", StringComparison.OrdinalIgnoreCase);
        Func<CronjobRow, string> selector = orderBy switch
        {
            "status" => row => row.Status ?? string.Empty,
            "createdAt" => row => row.LastRecordTime ?? string.Empty,
            _ => row => row.Name
        };

        var comparer = StringComparer.OrdinalIgnoreCase;
        var ordered = descending
            ? rows.OrderByDescending(selector, comparer).ThenBy(row => row.NodeName, comparer)
            : rows.OrderBy(selector, comparer).ThenBy(row => row.NodeName, comparer);
        return ordered.ToArray();
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
            throw new CronjobNodeNotFoundException(nodeId.Value);
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
                    "计划任务只读请求异常 节点 {NodeId} 类型 {Type}",
                    node.Id,
                    exception.GetType().Name);
                results[index] = new NodeCallResult<T>(
                    node,
                    default,
                    Error(node, PanelErrorKind.Protocol, ResourceFieldMapper.Truncate(exception.Message, 200) ?? "未知错误"),
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

    private static bool IsRecordNotFound(PanelError error) =>
        error.Message.Contains("record not found", StringComparison.OrdinalIgnoreCase);

    private static PanelError Canceled(NodeRecord node) => Error(node, PanelErrorKind.Canceled, "请求已取消");

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

    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static int ToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed record NodeCallResult<T>(
        NodeRecord Node,
        T? Value,
        PanelError? Error,
        TimeSpan Elapsed);
}
