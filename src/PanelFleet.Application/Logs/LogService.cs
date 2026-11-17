using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Logs;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Logs;

/// <summary>P10 系统日志查询（只读）：面板/系统/容器/网站四个范围，多节点并发聚合、按节点隔离错误。</summary>
public interface ILogService
{
    /// <summary>按范围查询日志；容器/网站范围需要对应上下文参数。</summary>
    Task<LogQueryResult> QueryAsync(LogQuery query, CancellationToken cancellationToken);

    /// <summary>读取系统日志过滤选项（运行中服务、可用日志文件日期）。</summary>
    Task<LogFilterOptions> GetFilterOptionsAsync(Guid? nodeId, CancellationToken cancellationToken);
}

/// <summary>
/// 日志查询服务：逐节点并发调用 1Panel（并发上限/单节点超时/整轮预算），单节点失败不影响其他节点；
/// 结果统一行模型并对行数、单行长度与总字节数做上限截断。不落盘、不跟随流。
/// </summary>
public sealed class LogService : ILogService
{
    private const int MaxLineLength = 8192;

    private const int DefaultLimit = 200;

    private readonly INodeStore _nodes;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly IContainerLogClientFactory _containerLogFactory;
    private readonly LogOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LogService> _logger;

    public LogService(
        INodeStore nodes,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        IContainerLogClientFactory containerLogFactory,
        LogOptions options,
        TimeProvider time,
        ILogger<LogService> logger)
    {
        _nodes = nodes;
        _protector = protector;
        _clientFactory = clientFactory;
        _containerLogFactory = containerLogFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<LogQueryResult> QueryAsync(LogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        var limit = NormalizeLimit(query.Limit);
        var nodes = await ResolveNodesAsync(query.NodeId, cancellationToken).ConfigureAwait(false);

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(_options.Budget);
        using var semaphore = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);

        var tasks = nodes
            .Select(node => QueryNodeWithGateAsync(node, limit, query, semaphore, budgetCts.Token, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return new LogQueryResult(
            query.Scope,
            results,
            results.Length,
            results.Count(result => result.Success),
            results.Count(result => !result.Success),
            results.Sum(result => result.Entries.Count),
            limit,
            NormalizeText(query.Keyword),
            NormalizeText(query.Container),
            NormalizeText(query.Website),
            query.Scope == LogScope.Panel ? NormalizePanelLog(query.PanelLog) : null,
            query.Scope == LogScope.Website ? NormalizeLogType(query.LogType) : null,
            query.StartTime,
            query.EndTime,
            _time.GetUtcNow());
    }

    public async Task<LogFilterOptions> GetFilterOptionsAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var nodes = await ResolveNodesAsync(nodeId, cancellationToken).ConfigureAwait(false);

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(_options.Budget);
        using var semaphore = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);

        var tasks = nodes
            .Select(node => FilterNodeWithGateAsync(node, semaphore, budgetCts.Token, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var services = results
            .SelectMany(result => result.Services)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = results
            .SelectMany(result => result.Files)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LogFilterOptions(
            services,
            files,
            results.Length,
            results.Count(result => result.Failed),
            _time.GetUtcNow());
    }

    private async Task<LogNodeResult> QueryNodeWithGateAsync(
        NodeRecord node,
        int limit,
        LogQuery query,
        SemaphoreSlim semaphore,
        CancellationToken budgetToken,
        CancellationToken cancellationToken)
    {
        try
        {
            await semaphore.WaitAsync(budgetToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(node, "整轮查询预算已用完，未执行该节点。", PanelErrorKind.Timeout, TimeSpan.Zero);
        }

        try
        {
            return await QueryNodeAsync(node, limit, query, budgetToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<LogNodeResult> QueryNodeAsync(
        NodeRecord node,
        int limit,
        LogQuery query,
        CancellationToken budgetToken,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var nodeCts = CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
        nodeCts.CancelAfter(_options.NodeTimeout);

        try
        {
            using var client = _clientFactory.Create(BuildDescriptor(node));
            var payload = query.Scope switch
            {
                LogScope.Panel => await QueryPanelAsync(client, node, limit, query, nodeCts.Token).ConfigureAwait(false),
                LogScope.System => await QuerySystemAsync(client, node, limit, query, nodeCts.Token).ConfigureAwait(false),
                LogScope.Container => await QueryContainerAsync(node, limit, query, nodeCts.Token).ConfigureAwait(false),
                _ => await QueryWebsiteAsync(client, node, limit, query, nodeCts.Token).ConfigureAwait(false)
            };

            return new LogNodeResult(
                node.Id.Value,
                node.Name,
                payload.Error is null ? LogNodeState.Success : LogNodeState.Failed,
                payload.Entries,
                payload.Truncated,
                payload.Total,
                payload.HasMore,
                payload.NextCursor,
                payload.Error,
                payload.ErrorMessage,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(
                node,
                $"读取日志超时（超过 {_options.NodeTimeoutSeconds} 秒）。",
                PanelErrorKind.Timeout,
                stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "读取节点日志异常 节点 {NodeId} 范围 {Scope}", node.Id, query.Scope);
            return Failed(
                node,
                "读取日志异常：" + exception.Message,
                PanelErrorKind.Protocol,
                stopwatch.Elapsed);
        }
    }

    private async Task<NodePayload> QueryPanelAsync(
        IPanelClient client,
        NodeRecord node,
        int limit,
        LogQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(limit, 1, 200);
        var login = string.Equals(NormalizePanelLog(query.PanelLog), "login", StringComparison.Ordinal);
        var keyword = NormalizeText(query.Keyword);

        if (login)
        {
            var body = new
            {
                page = 1,
                pageSize,
                info = keyword,
                startTime = query.StartTime?.ToString("O", CultureInfo.InvariantCulture),
                endTime = query.EndTime?.ToString("O", CultureInfo.InvariantCulture)
            };
            var result = await client
                .PostAsync<PagedLogItemsDto<PanelLoginLogDto>>(LogRoutes.PanelLogin, body, cancellationToken)
                .ConfigureAwait(false);
            if (result.Error is not null)
            {
                return NodePayload.FromError(result.Error);
            }

            var entries = (result.Data?.Items ?? []).Select(MapLoginEntry).ToList();
            var filtered = ApplyFilters(entries, query, limit, out var truncated);
            return new NodePayload(filtered, truncated, result.Data?.Total, false, null, null, null);
        }

        var operationBody = new { page = 1, pageSize };
        var operation = await client
            .PostAsync<PagedLogItemsDto<PanelOperationLogDto>>(LogRoutes.PanelOperation, operationBody, cancellationToken)
            .ConfigureAwait(false);
        if (operation.Error is not null)
        {
            return NodePayload.FromError(operation.Error);
        }

        var operationEntries = (operation.Data?.Items ?? []).Select(MapOperationEntry).ToList();
        var limited = ApplyFilters(operationEntries, query, limit, out var truncatedOperation);
        return new NodePayload(limited, truncatedOperation, operation.Data?.Total, false, null, null, null);
    }

    private async Task<NodePayload> QuerySystemAsync(
        IPanelClient client,
        NodeRecord node,
        int limit,
        LogQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(limit, 1, 500);
        var body = new
        {
            pageSize,
            cursor = NormalizeText(query.Cursor),
            startTime = query.StartTime?.ToString("O", CultureInfo.InvariantCulture),
            endTime = query.EndTime?.ToString("O", CultureInfo.InvariantCulture),
            keyword = NormalizeText(query.Keyword),
            priority = NormalizeText(query.Priority),
            service = NormalizeText(query.Service)
        };

        var result = await client
            .PostAsync<SystemLogPageDto>(LogRoutes.SystemRead, body, cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null)
        {
            return NodePayload.FromError(result.Error);
        }

        var entries = (result.Data?.Items ?? []).Select(MapSystemEntry).ToList();
        var filtered = ApplyFilters(entries, query, limit, out var truncated);
        var hasMore = result.Data?.HasMore ?? false;
        return new NodePayload(
            filtered,
            truncated || hasMore,
            null,
            hasMore,
            result.Data?.NextCursor,
            null,
            null);
    }

    private async Task<NodePayload> QueryContainerAsync(
        NodeRecord node,
        int limit,
        LogQuery query,
        CancellationToken cancellationToken)
    {
        var container = query.Container!.Trim();
        var since = query.StartTime is { } start
            ? start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "all";

        using var logClient = _containerLogFactory.Create(BuildDescriptor(node));
        var read = await logClient
            .ReadAsync(
                new ContainerLogQuery(container, Compose: null, since, Math.Min(limit, 2000), Timestamp: true),
                cancellationToken)
            .ConfigureAwait(false);

        if (read.Error is not null)
        {
            return new NodePayload(
                [],
                read.Truncated,
                null,
                false,
                null,
                read.Error,
                read.ErrorMessage ?? read.Error.Message);
        }

        var entries = read.Lines
            .Select(line => new LogEntry(Time: null, Level: null, Service: container, line, Detail: null))
            .ToList();
        var filtered = ApplyFilters(entries, query, limit, out var truncated);
        return new NodePayload(filtered, truncated || read.Truncated, null, false, null, null, null);
    }

    private async Task<NodePayload> QueryWebsiteAsync(
        IPanelClient client,
        NodeRecord node,
        int limit,
        LogQuery query,
        CancellationToken cancellationToken)
    {
        var input = query.Website!.Trim();
        long siteId;
        string siteLabel;

        if (long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
        {
            siteId = numericId;
            siteLabel = input;
        }
        else
        {
            var search = await client
                .PostAsync<WebsiteLogTargetPageDto>(
                    LogRoutes.WebsiteSearch,
                    new
                    {
                        page = 1,
                        pageSize = 20,
                        name = input,
                        orderBy = "created_at",
                        order = "descending"
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (search.Error is not null)
            {
                return NodePayload.FromError(search.Error);
            }

            var target = FindWebsite(search.Data?.Items, input);
            if (target is null)
            {
                return new NodePayload(
                    [],
                    false,
                    null,
                    false,
                    null,
                    new PanelError(PanelErrorKind.NotFound, $"[{node.Name}] 未找到网站：{input}"),
                    $"节点未找到网站「{input}」。");
            }

            siteId = target.Id;
            siteLabel = FirstNonEmpty(target.PrimaryDomain, target.Alias, input);
        }

        var logType = string.Equals(NormalizeLogType(query.LogType), "error", StringComparison.Ordinal)
            ? "error.log"
            : "access.log";
        var pageSize = Math.Clamp(limit, 1, 500);
        var result = await client
            .PostAsync<WebsiteLogDto>(
                LogRoutes.WebsiteLog,
                new { id = siteId, logType, page = 1, pageSize },
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null)
        {
            return NodePayload.FromError(result.Error);
        }

        var content = result.Data?.Content;
        var lines = SplitLines(content);
        var truncated = result.Data is { End: false } || lines.Count > limit;
        if (lines.Count > limit)
        {
            lines = lines.Skip(lines.Count - limit).ToList();
        }

        var level = logType == "error.log" ? "错误" : "访问";
        var entries = lines
            .Select(line => new LogEntry(Time: null, Level: level, Service: siteLabel, line, Detail: null))
            .ToList();
        var filtered = ApplyFilters(entries, query, limit, out var truncatedByLimits);
        return new NodePayload(filtered, truncated || truncatedByLimits, null, false, null, null, null);
    }

    private async Task<FilterNodeResult> FilterNodeWithGateAsync(
        NodeRecord node,
        SemaphoreSlim semaphore,
        CancellationToken budgetToken,
        CancellationToken cancellationToken)
    {
        try
        {
            await semaphore.WaitAsync(budgetToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FilterNodeResult([], [], Failed: true);
        }

        try
        {
            using var nodeCts = CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
            nodeCts.CancelAfter(_options.NodeTimeout);
            using var client = _clientFactory.Create(BuildDescriptor(node));

            var servicesTask = client.GetAsync<string[]>(LogRoutes.SystemServices, nodeCts.Token);
            var filesTask = client.GetAsync<string[]>(LogRoutes.SystemFiles, nodeCts.Token);
            await Task.WhenAll(servicesTask, filesTask).ConfigureAwait(false);

            var services = await servicesTask.ConfigureAwait(false);
            var files = await filesTask.ConfigureAwait(false);
            var failed = services.Error is not null || files.Error is not null;
            return new FilterNodeResult(services.Data ?? [], files.Data ?? [], failed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "读取节点日志过滤选项失败 节点 {NodeId}", node.Id);
            return new FilterNodeResult([], [], Failed: true);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<IReadOnlyList<NodeRecord>> ResolveNodesAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        if (nodeId is { } id)
        {
            var record = records.FirstOrDefault(item => item.Id.Value == id);
            if (record is null || !record.IsEnabled)
            {
                throw new LogNodeNotFoundException(id);
            }

            return [record];
        }

        return records.Where(record => record.IsEnabled).ToArray();
    }

    private static void ValidateQuery(LogQuery query)
    {
        if (query.Scope == LogScope.Container && string.IsNullOrWhiteSpace(query.Container))
        {
            throw new LogValidationException("容器日志查询需要指定容器名（container）。");
        }

        if (query.Scope == LogScope.Website && string.IsNullOrWhiteSpace(query.Website))
        {
            throw new LogValidationException("网站日志查询需要指定网站域名或 id（website）。");
        }

        if (query.StartTime is { } start && query.EndTime is { } end && start > end)
        {
            throw new LogValidationException("开始时间不能晚于结束时间。");
        }
    }

    private int NormalizeLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, _options.MaxLines);

    private List<LogEntry> ApplyFilters(
        IEnumerable<LogEntry> entries,
        LogQuery query,
        int limit,
        out bool truncated)
    {
        truncated = false;
        var keyword = NormalizeText(query.Keyword);
        var result = new List<LogEntry>(Math.Min(limit, 256));
        long bytes = 0;

        foreach (var entry in entries)
        {
            if (keyword is { Length: > 0 } && !ContainsKeyword(entry, keyword))
            {
                continue;
            }

            if (query.StartTime is { } start && entry.Timestamp is { } entryTime && entryTime < start)
            {
                continue;
            }

            if (query.EndTime is { } end && entry.Timestamp is { } entryEndTime && entryEndTime > end)
            {
                continue;
            }

            var message = TruncateLine(entry.Message);
            var detail = entry.Detail is null ? null : TruncateLine(entry.Detail);
            if (message.Length < entry.Message.Length || (entry.Detail is { } rawDetail && detail!.Length < rawDetail.Length))
            {
                truncated = true;
            }

            var size = Encoding.UTF8.GetByteCount(message)
                + (detail is null ? 0 : Encoding.UTF8.GetByteCount(detail));
            if (result.Count >= limit || bytes + size > _options.MaxBytes)
            {
                truncated = true;
                break;
            }

            result.Add(entry with { Message = message, Detail = detail });
            bytes += size;
        }

        return result;
    }

    private static bool ContainsKeyword(LogEntry entry, string keyword) =>
        entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || (entry.Detail?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
        || (entry.Service?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
        || (entry.Level?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);

    private static LogEntry MapOperationEntry(PanelOperationLogDto item)
    {
        var message = FirstNonEmpty(item.DetailZH, item.Message, item.Path, "（无描述）");
        var detailParts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(item.Method))
        {
            detailParts.Add(item.Method.Trim().ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(item.Path))
        {
            detailParts.Add(item.Path.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.User))
        {
            detailParts.Add(item.User.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.Ip))
        {
            detailParts.Add(item.Ip.Trim());
        }

        if (item.Latency is { } latency && latency > 0)
        {
            detailParts.Add((latency / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + " ms");
        }

        return new LogEntry(
            FormatTime(item.CreatedAt),
            item.Status,
            item.Source,
            message,
            detailParts.Count == 0 ? null : string.Join(" · ", detailParts),
            item.CreatedAt);
    }

    private static LogEntry MapLoginEntry(PanelLoginLogDto item)
    {
        var message = FirstNonEmpty(
            item.Message,
            string.Equals(item.Status, "Success", StringComparison.OrdinalIgnoreCase) ? "登录成功" : "登录失败");
        var detailParts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(item.User))
        {
            detailParts.Add(item.User.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.Ip))
        {
            detailParts.Add(item.Ip.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.Address))
        {
            detailParts.Add(item.Address.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.Agent))
        {
            detailParts.Add(TruncateLine(item.Agent.Trim()));
        }

        return new LogEntry(
            FormatTime(item.CreatedAt),
            item.Status,
            "登录",
            message,
            detailParts.Count == 0 ? null : string.Join(" · ", detailParts),
            item.CreatedAt);
    }

    private static LogEntry MapSystemEntry(SystemLogItemDto item) => new(
        string.IsNullOrWhiteSpace(item.Time) ? null : item.Time.Trim(),
        MapPriority(item.Priority),
        string.IsNullOrWhiteSpace(item.Service) ? null : item.Service.Trim(),
        FirstNonEmpty(item.Message, "（空消息）"),
        null,
        null);

    private static string? MapPriority(string? priority) => priority?.Trim() switch
    {
        "0" => "紧急",
        "1" => "警报",
        "2" => "严重",
        "3" => "错误",
        "4" => "警告",
        "5" => "通知",
        "6" => "信息",
        "7" => "调试",
        var other => string.IsNullOrWhiteSpace(other) ? null : other
    };

    private static WebsiteLogTargetDto? FindWebsite(
        IReadOnlyList<WebsiteLogTargetDto>? items,
        string input)
    {
        if (items is not { Count: > 0 })
        {
            return null;
        }

        var exact = items.FirstOrDefault(item =>
            string.Equals(item.PrimaryDomain, input, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Alias, input, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var contains = items.FirstOrDefault(item =>
            (item.PrimaryDomain?.Contains(input, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Alias?.Contains(input, StringComparison.OrdinalIgnoreCase) ?? false));
        return contains ?? (items.Count == 1 ? items[0] : null);
    }

    private static List<string> SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length == 0 && result.Count == lines.Length - 1)
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    private static string TruncateLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= MaxLineLength ? value : value[..MaxLineLength] + "…";
    }

    private static string FormatTime(DateTimeOffset? time) =>
        time is null ? string.Empty : time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizePanelLog(string? value) =>
        string.Equals(value?.Trim(), "login", StringComparison.OrdinalIgnoreCase) ? "login" : "operation";

    private static string NormalizeLogType(string? value) =>
        string.Equals(value?.Trim(), "error", StringComparison.OrdinalIgnoreCase) ? "error" : "access";

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static LogNodeResult Failed(
        NodeRecord node,
        string message,
        PanelErrorKind kind,
        TimeSpan elapsed) => new(
        node.Id.Value,
        node.Name,
        LogNodeState.Failed,
        [],
        false,
        null,
        false,
        null,
        new PanelError(kind, message),
        message,
        elapsed);

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    /// <summary>单节点日志查询载荷（错误与数据互斥）。</summary>
    private sealed record NodePayload(
        IReadOnlyList<LogEntry> Entries,
        bool Truncated,
        long? Total,
        bool HasMore,
        string? NextCursor,
        PanelError? Error,
        string? ErrorMessage)
    {
        public static NodePayload FromError(PanelError error) =>
            new([], false, null, false, null, error, error.Message);
    }

    private sealed record FilterNodeResult(
        IReadOnlyList<string> Services,
        IReadOnlyList<string> Files,
        bool Failed);
}
