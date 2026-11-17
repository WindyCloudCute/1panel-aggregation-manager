using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Overview;

/// <summary>聚合总览查询与刷新（scoped）：读取节点、解密 Key、并发采集、更新缓存并广播。</summary>
public sealed class OverviewService : IOverviewService
{
    private readonly INodeStore _nodes;
    private readonly INodeGroupStore _groups;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly IFanoutEngine _fanout;
    private readonly IOverviewCache _cache;
    private readonly IOverviewBroadcaster _broadcaster;
    private readonly INodeCircuitBreaker _circuit;
    private readonly OverviewRefreshGates _gates;
    private readonly OverviewOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OverviewService> _logger;

    public OverviewService(
        INodeStore nodes,
        INodeGroupStore groups,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        IFanoutEngine fanout,
        IOverviewCache cache,
        IOverviewBroadcaster broadcaster,
        INodeCircuitBreaker circuit,
        OverviewRefreshGates gates,
        OverviewOptions options,
        TimeProvider time,
        ILogger<OverviewService> logger)
    {
        _nodes = nodes;
        _groups = groups;
        _protector = protector;
        _clientFactory = clientFactory;
        _fanout = fanout;
        _cache = cache;
        _broadcaster = broadcaster;
        _circuit = circuit;
        _gates = gates;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<FleetOverview> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var (records, groupNames) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return BuildOverview(records, groupNames);
    }

    public async Task<FleetOverview> RefreshAllAsync(CancellationToken cancellationToken)
    {
        var (records, groupNames) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var enabled = records.Where(record => record.IsEnabled).ToArray();
        var nodeById = records.ToDictionary(record => record.Id);
        var startedAt = _time.GetUtcNow();

        _cache.SetRefreshing(true);
        _broadcaster.Publish(new OverviewEvent.RunStarted(enabled.Length, startedAt));

        var successes = 0;
        var failures = 0;
        var skipped = 0;
        var budgetExceeded = false;
        TimeSpan elapsed;

        try
        {
            // 禁用节点：不采集，直接广播 Disabled 快照。
            var cacheSnapshot = _cache.GetSnapshot();
            foreach (var record in records.Where(record => !record.IsEnabled))
            {
                _broadcaster.Publish(new OverviewEvent.NodeUpdated(
                    BuildSnapshot(record, groupNames, cacheSnapshot, startedAt)));
            }

            var targets = new List<FanoutTarget<DashboardBaseDto>>();
            foreach (var record in enabled)
            {
                var now = _time.GetUtcNow();
                if (!_circuit.TryEnter(record.Id, now, force: false))
                {
                    skipped++;
                    _broadcaster.Publish(new OverviewEvent.NodeUpdated(
                        BuildSnapshot(record, groupNames, cacheSnapshot, now)));
                    continue;
                }

                var descriptor = BuildDescriptor(record);
                var nodeId = record.Id;
                var nodeName = record.Name;
                targets.Add(new FanoutTarget<DashboardBaseDto>(
                    nodeId,
                    nodeName,
                    async token =>
                    {
                        using var lease = await _gates.AcquireAsync(nodeId, token).ConfigureAwait(false);
                        using var client = _clientFactory.Create(descriptor);
                        var result = await client.GetDashboardBaseAsync("all", "all", token).ConfigureAwait(false);
                        if (result.Error is not null)
                        {
                            // 外部取消（单节点超时/整轮预算）时 PanelClient 返回 Canceled；
                            // 转成 OCE，让 FanoutEngine 归类为 TimedOut/BudgetExceeded。
                            if (result.Error.Kind == PanelErrorKind.Canceled && token.IsCancellationRequested)
                            {
                                throw new OperationCanceledException(token);
                            }

                            throw new PanelCallException(result.Error);
                        }

                        return result.Data
                            ?? throw new PanelCallException(
                                new PanelError(PanelErrorKind.Protocol, $"[{nodeName}] 响应为空。"));
                    }));
            }

            await foreach (var result in _fanout
                               .RunAsync(targets, BuildFanoutOptions(), cancellationToken)
                               .ConfigureAwait(false))
            {
                var attemptedAt = _time.GetUtcNow();
                var previous = FindData(result.NodeId);

                NodeMetrics? metrics;
                PanelError? error;
                var success = result.Status == FanoutStatus.Success;

                if (success)
                {
                    metrics = OverviewMapper.Map(result.Data, attemptedAt);
                    error = null;
                    successes++;
                }
                else
                {
                    metrics = previous?.Metrics;
                    error = result.Error
                        ?? new PanelError(PanelErrorKind.Timeout, $"[{result.NodeId}] 采集失败。");
                    failures++;
                }

                if (result.Status == FanoutStatus.BudgetExceeded)
                {
                    budgetExceeded = true;
                }

                var update = new NodeOverviewData(
                    metrics,
                    error,
                    success ? attemptedAt : previous?.FetchedAt,
                    attemptedAt,
                    result.Elapsed,
                    success);
                _cache.ApplyNode(result.NodeId, update);

                if (success)
                {
                    _circuit.RecordSuccess(result.NodeId);
                }
                else if (result.Status is FanoutStatus.TimedOut or FanoutStatus.Failed)
                {
                    // BudgetExceeded 是全局条件，不归咎于单节点，避免误开熔断。
                    _circuit.RecordFailure(result.NodeId, error!.Kind, attemptedAt);
                }

                if (nodeById.TryGetValue(result.NodeId, out var record))
                {
                    _broadcaster.Publish(new OverviewEvent.NodeUpdated(
                        BuildSnapshot(record, groupNames, _cache.GetSnapshot(), _time.GetUtcNow())));
                }
            }

            _cache.Prune(records.Select(record => record.Id).ToHashSet());
            elapsed = _time.GetUtcNow() - startedAt;
            _cache.MarkRunCompleted(elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "总览整轮刷新失败。");
            elapsed = _time.GetUtcNow() - startedAt;
        }
        finally
        {
            _cache.SetRefreshing(false);
        }

        var overview = await GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        _broadcaster.Publish(new OverviewEvent.RunCompleted(overview, elapsed, budgetExceeded));

        _logger.LogInformation(
            "总览刷新完成：启用节点 {Enabled}，成功 {Success}，失败 {Failure}，跳过 {Skipped}，耗时 {ElapsedMs}ms。",
            enabled.Length,
            successes,
            failures,
            skipped,
            (int)elapsed.TotalMilliseconds);

        return overview;
    }

    public async Task<NodeOverviewSnapshot> RefreshNodeAsync(
        NodeId nodeId,
        bool force,
        CancellationToken cancellationToken)
    {
        var (records, groupNames) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var record = records.FirstOrDefault(item => item.Id == nodeId)
            ?? throw new NodeNotFoundException(nodeId);

        var now = _time.GetUtcNow();
        if (!record.IsEnabled)
        {
            return BuildSnapshot(record, groupNames, _cache.GetSnapshot(), now);
        }

        if (!force && !_circuit.TryEnter(nodeId, now, force: false))
        {
            return BuildSnapshot(record, groupNames, _cache.GetSnapshot(), _time.GetUtcNow());
        }

        using var lease = await _gates.AcquireAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.NodeTimeout);

        var stopwatch = Stopwatch.StartNew();
        NodeMetrics? metrics = null;
        PanelError? error = null;
        var success = false;

        try
        {
            using var client = _clientFactory.Create(BuildDescriptor(record));
            var result = await client
                .GetDashboardBaseAsync("all", "all", timeoutCts.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (result.Error is null)
            {
                metrics = OverviewMapper.Map(result.Data, _time.GetUtcNow());
                success = true;
            }
            else
            {
                error = NormalizeError(result.Error, record.Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _circuit.RecordFailure(nodeId, PanelErrorKind.Canceled, _time.GetUtcNow());
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            error = TimeoutError(record.Name, _options.NodeTimeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stopwatch.Stop();
            error = new PanelError(
                PanelErrorKind.Protocol,
                $"[{record.Name}] 采集失败：{exception.Message}",
                Cause: exception);
        }

        var attemptedAt = _time.GetUtcNow();
        var previous = FindData(nodeId);
        var update = new NodeOverviewData(
            success ? metrics : previous?.Metrics,
            error,
            success ? attemptedAt : previous?.FetchedAt,
            attemptedAt,
            stopwatch.Elapsed,
            success);

        _cache.ApplyNode(nodeId, update);

        if (success)
        {
            _circuit.RecordSuccess(nodeId);
        }
        else if (error is not null)
        {
            _circuit.RecordFailure(nodeId, error.Kind, attemptedAt);
        }

        var snapshot = BuildSnapshot(record, groupNames, _cache.GetSnapshot(), _time.GetUtcNow());
        _broadcaster.Publish(new OverviewEvent.NodeUpdated(snapshot));
        return snapshot;
    }

    private async Task<(IReadOnlyList<NodeRecord> Records, IReadOnlyDictionary<Guid, string> GroupNames)> LoadAsync(
        CancellationToken cancellationToken)
    {
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var groups = await _groups.ListAsync(cancellationToken).ConfigureAwait(false);
        var groupNames = groups.ToDictionary(group => group.Id, group => group.Name);
        return (records, groupNames);
    }

    private FleetOverview BuildOverview(
        IReadOnlyList<NodeRecord> records,
        IReadOnlyDictionary<Guid, string> groupNames)
    {
        var cache = _cache.GetSnapshot();
        var now = _time.GetUtcNow();
        var snapshots = records
            .Select(record => BuildSnapshot(record, groupNames, cache, now))
            .ToArray();

        return OverviewSnapshotFactory.Build(
            snapshots,
            now,
            cache.IsRefreshing,
            cache.LastRunElapsed,
            cache.Generation);
    }

    private NodeOverviewSnapshot BuildSnapshot(
        NodeRecord record,
        IReadOnlyDictionary<Guid, string> groupNames,
        OverviewCacheSnapshot cache,
        DateTimeOffset now)
    {
        var groupName = record.GroupId is { } groupId && groupNames.TryGetValue(groupId, out var name)
            ? name
            : null;
        cache.Nodes.TryGetValue(record.Id, out var data);
        var circuit = _circuit.GetStatus(record.Id, now);

        return OverviewSnapshotFactory.Create(record, groupName, data, circuit, now, _options.StaleAfter);
    }

    private NodeOverviewData? FindData(NodeId nodeId)
    {
        var cache = _cache.GetSnapshot();
        return cache.Nodes.TryGetValue(nodeId, out var data) ? data : null;
    }

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    private FanoutOptions BuildFanoutOptions() => new()
    {
        MaxConcurrency = _options.MaxConcurrency,
        PerTargetTimeout = _options.NodeTimeout,
        Budget = _options.Budget
    };

    private PanelError NormalizeError(PanelError error, string nodeName) =>
        error.Kind == PanelErrorKind.Canceled
            ? TimeoutError(nodeName, _options.NodeTimeout)
            : error;

    private static PanelError TimeoutError(string nodeName, TimeSpan timeout) => new(
        PanelErrorKind.Timeout,
        $"[{nodeName}] 节点响应超时（{timeout.TotalSeconds:0.#} 秒）。");
}
