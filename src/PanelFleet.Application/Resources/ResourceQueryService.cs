using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Resources;

/// <summary>P4 资源清单查询/刷新/详情（scoped）。</summary>
public sealed class ResourceQueryService : IResourceQueryService
{
    private readonly INodeStore _nodes;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly IFanoutEngine _fanout;
    private readonly IReadOnlyDictionary<ResourceKind, IResourceLoader> _loaders;
    private readonly IResourceCache _cache;
    private readonly IResourceAccessTracker _accessTracker;
    private readonly ResourceGates _gates;
    private readonly ResourceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ResourceQueryService> _logger;

    public ResourceQueryService(
        INodeStore nodes,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        IFanoutEngine fanout,
        IEnumerable<IResourceLoader> loaders,
        IResourceCache cache,
        IResourceAccessTracker accessTracker,
        ResourceGates gates,
        ResourceOptions options,
        TimeProvider time,
        ILogger<ResourceQueryService> logger)
    {
        _nodes = nodes;
        _protector = protector;
        _clientFactory = clientFactory;
        _fanout = fanout;
        _loaders = loaders.ToDictionary(loader => loader.Kind);
        _cache = cache;
        _accessTracker = accessTracker;
        _gates = gates;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<ResourcePage> QueryAsync(
        ResourceKind kind,
        ResourceQuery query,
        ResourceQueryMode mode,
        CancellationToken cancellationToken)
    {
        _accessTracker.MarkAccessed(kind);
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var toLoad = new List<NodeRecord>();

        if (mode == ResourceQueryMode.EnsureFresh)
        {
            foreach (var record in records.Where(record => record.IsEnabled))
            {
                if (!_cache.TryGet(record.Id, kind, out var cached)
                    || cached.AttemptedAt is null
                    || now - cached.AttemptedAt.Value > _options.CacheTtl)
                {
                    toLoad.Add(record);
                }
            }
        }

        if (toLoad.Count > 0)
        {
            await LoadNodesAsync(kind, toLoad, cancellationToken).ConfigureAwait(false);
        }

        var results = BuildNodeResults(kind, records);
        var page = BuildPage(kind, query, results);
        _cache.Prune(records.Select(record => record.Id).ToHashSet());
        return page;
    }

    public async Task<ResourcePage> RefreshAsync(ResourceKind kind, ResourceQuery query, CancellationToken cancellationToken)
    {
        _accessTracker.MarkAccessed(kind);
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var enabled = records.Where(record => record.IsEnabled).ToArray();
        await LoadNodesAsync(kind, enabled, cancellationToken).ConfigureAwait(false);

        var results = BuildNodeResults(kind, records);
        var page = BuildPage(kind, query, results);
        _cache.Prune(records.Select(record => record.Id).ToHashSet());
        return page;
    }

    public async Task<ResourcePage> RefreshNodeAsync(
        ResourceKind kind,
        NodeId nodeId,
        ResourceQuery query,
        CancellationToken cancellationToken)
    {
        _accessTracker.MarkAccessed(kind);
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var record = records.FirstOrDefault(item => item.Id == nodeId)
            ?? throw new NodeNotFoundException(nodeId);

        await LoadNodesAsync(kind, [record], cancellationToken).ConfigureAwait(false);

        var results = BuildNodeResults(kind, records);
        return BuildPage(kind, query with { NodeId = nodeId }, results);
    }

    public async Task<ResourceDetail> GetDetailAsync(
        ResourceKind kind,
        NodeId nodeId,
        string sourceId,
        CancellationToken cancellationToken)
    {
        _accessTracker.MarkAccessed(kind);
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var record = records.FirstOrDefault(item => item.Id == nodeId)
            ?? throw new NodeNotFoundException(nodeId);

        var now = _time.GetUtcNow();
        if (_cache.TryGetDetail(nodeId, kind, sourceId, out var cached)
            && now - cached.FetchedAt <= _options.DetailCacheTtl)
        {
            return cached;
        }

        if (!_loaders.TryGetValue(kind, out var loader))
        {
            return new ResourceDetail(
                nodeId,
                record.Name,
                kind,
                sourceId,
                sourceId,
                new Dictionary<string, string?>(),
                now,
                new PanelError(PanelErrorKind.Unsupported, "该资源类型不支持详情。"));
        }

        using var client = _clientFactory.Create(BuildDescriptor(record));
        var detail = await loader.LoadDetailAsync(client, record, sourceId, cancellationToken).ConfigureAwait(false);
        if (detail.Error is null)
        {
            _cache.SetDetail(detail);
        }

        return detail;
    }

    private async Task LoadNodesAsync(
        ResourceKind kind,
        IReadOnlyList<NodeRecord> records,
        CancellationToken cancellationToken)
    {
        if (!_loaders.TryGetValue(kind, out var loader))
        {
            _logger.LogWarning("资源类型 {Kind} 没有加载器。", kind);
            return;
        }

        var targets = records.Select(record => new FanoutTarget<NodeResourceResult>(
            record.Id,
            record.Name,
            token => LoadNodeAsync(loader, kind, record, token))).ToArray();

        var options = new FanoutOptions
        {
            MaxConcurrency = _options.MaxConcurrency,
            PerTargetTimeout = _options.NodeTimeout,
            Budget = _options.Budget
        };

        await foreach (var result in _fanout.RunAsync(targets, options, cancellationToken).ConfigureAwait(false))
        {
            NodeResourceResult nodeResult;
            if (result.Status == FanoutStatus.Success && result.Data is not null)
            {
                nodeResult = result.Data;
            }
            else
            {
                var error = result.Error
                    ?? new PanelError(PanelErrorKind.Timeout, "资源采集失败。");
                _cache.TryGet(result.NodeId, kind, out var previous);
                nodeResult = MergeFailure(kind, result.NodeId, records, previous, error, result.Elapsed);
                _logger.LogWarning(
                    "资源采集失败：节点 {NodeId} 类型 {Kind} 状态 {Status} 错误 {Error}",
                    result.NodeId,
                    kind,
                    result.Status,
                    error.Kind);
            }

            _cache.Apply(nodeResult);
        }
    }

    private async Task<NodeResourceResult> LoadNodeAsync(
        IResourceLoader loader,
        ResourceKind kind,
        NodeRecord record,
        CancellationToken cancellationToken)
    {
        if (!_gates.TryEnter(record.Id, kind))
        {
            return _cache.TryGet(record.Id, kind, out var cached)
                ? cached
                : Pending(record, kind);
        }

        try
        {
            using var client = _clientFactory.Create(BuildDescriptor(record));
            return await loader.LoadAsync(client, record, _options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gates.Exit(record.Id, kind);
        }
    }

    private NodeResourceResult MergeFailure(
        ResourceKind kind,
        NodeId nodeId,
        IReadOnlyList<NodeRecord> records,
        NodeResourceResult? previous,
        PanelError error,
        TimeSpan elapsed)
    {
        var record = records.FirstOrDefault(item => item.Id == nodeId);
        var name = record?.Name ?? previous?.NodeName ?? nodeId.ToString();
        var rows = previous?.Rows ?? [];
        var now = _time.GetUtcNow();
        return new NodeResourceResult(
            nodeId,
            name,
            kind,
            rows.Count > 0 ? ResourceNodeState.Partial : ResourceNodeState.Failed,
            rows,
            previous?.Total ?? 0,
            previous?.Truncated ?? false,
            previous?.Warnings ?? [],
            error,
            previous?.FetchedAt,
            now,
            elapsed);
    }

    private IReadOnlyList<NodeResourceResult> BuildNodeResults(ResourceKind kind, IReadOnlyList<NodeRecord> records)
    {
        var results = new List<NodeResourceResult>(records.Count);
        foreach (var record in records)
        {
            if (!record.IsEnabled)
            {
                results.Add(Skipped(record, kind));
                continue;
            }

            results.Add(_cache.TryGet(record.Id, kind, out var cached) ? cached : Pending(record, kind));
        }

        return results;
    }

    private static NodeResourceResult Pending(NodeRecord record, ResourceKind kind) => new(
        record.Id,
        record.Name,
        kind,
        ResourceNodeState.Pending,
        [],
        0,
        false,
        [],
        null,
        null,
        null,
        TimeSpan.Zero);

    private static NodeResourceResult Skipped(NodeRecord record, ResourceKind kind) => new(
        record.Id,
        record.Name,
        kind,
        ResourceNodeState.Skipped,
        [],
        0,
        false,
        [],
        null,
        null,
        null,
        TimeSpan.Zero);

    private ResourcePage BuildPage(
        ResourceKind kind,
        ResourceQuery query,
        IReadOnlyList<NodeResourceResult> results)
    {
        var now = _time.GetUtcNow();
        var rows = results
            .SelectMany(result => result.Rows)
            .Where(row => query.NodeId is not { } nodeId || row.NodeId == nodeId)
            .ToList();

        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            var filter = query.Filter.Trim();
            rows = rows
                .Where(row =>
                    row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.NodeName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.Attributes.Values.Any(value =>
                        value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                .ToList();
        }

        rows = SortRows(rows, query.Sort, query.Descending);
        if (rows.Count > _options.MergeLimit)
        {
            rows = rows.Take(_options.MergeLimit).ToList();
        }

        var totalMatched = rows.Count;
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 200);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalMatched / (double)pageSize));
        var page = Math.Clamp(query.Page <= 0 ? 1 : query.Page, 1, totalPages);
        var pageRows = rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return new ResourcePage(
            kind,
            now,
            pageRows,
            totalMatched,
            page,
            pageSize,
            totalPages,
            results.Count(result => result.State is ResourceNodeState.Success or ResourceNodeState.Partial),
            results.Count(result => result.State == ResourceNodeState.Failed),
            results.Count,
            results.OrderBy(result => result.NodeName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static List<ResourceRow> SortRows(List<ResourceRow> rows, string? sort, bool descending)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? "name" : sort.Trim();
        Func<ResourceRow, string?> selector = key.ToLowerInvariant() switch
        {
            "name" => row => row.Name,
            "status" => row => row.Status,
            "node" => row => row.NodeName,
            _ => row => row.Attributes.TryGetValue(key, out var value) ? value : null
        };

        var ordered = descending
            ? rows.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(selector, StringComparer.OrdinalIgnoreCase);
        return ordered.ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);
}
