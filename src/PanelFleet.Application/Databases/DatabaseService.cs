using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Databases;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;
using DatabaseInstanceDto = PanelFleet.Client.Dto.Databases.DatabaseInstanceDto;

namespace PanelFleet.Application.Databases;

/// <summary>P8 数据库只读查询：实例/库/用户/授权/字符集/状态/关联资源/Redis 信息。</summary>
public interface IDatabaseService
{
    /// <summary>跨节点采集「节点 → 实例 → 库」树。</summary>
    Task<DatabaseOverview> GetOverviewAsync(Guid? nodeId, CancellationToken cancellationToken);

    Task<DatabaseReadResult<DatabaseUserView>> GetUsersAsync(
        Guid nodeId,
        string instance,
        CancellationToken cancellationToken);

    Task<DatabaseReadResult<DatabaseGrantView>> GetGrantsAsync(
        Guid nodeId,
        string instance,
        CancellationToken cancellationToken);

    Task<DatabaseReadResult<DatabaseFormatView>> GetFormatsAsync(
        Guid nodeId,
        string instance,
        CancellationToken cancellationToken);

    Task<DatabaseReadResult<DatabaseLinkResourceView>> CheckDeleteAsync(
        Guid nodeId,
        DatabaseEngine engine,
        long databaseId,
        string instance,
        CancellationToken cancellationToken);

    Task<DatabaseStatusView> GetStatusAsync(
        Guid nodeId,
        DatabaseEngine engine,
        string instance,
        string from,
        CancellationToken cancellationToken);

    Task<DatabaseRedisView> GetRedisInfoAsync(
        Guid nodeId,
        string instance,
        string from,
        CancellationToken cancellationToken);
}

/// <summary>
/// 数据库只读服务：逐节点拉取引擎实例与实例下库列表；失败按引擎/实例/节点隔离。
/// <para>本地/应用实例专有能力（状态/变量/远程访问/基础信息）对远程实例返回 500，服务层优雅降级。</para>
/// </summary>
public sealed class DatabaseService : IDatabaseService
{
    private readonly INodeStore _nodeStore;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly DatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(
        INodeStore nodeStore,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        DatabaseOptions options,
        TimeProvider timeProvider,
        ILogger<DatabaseService> logger)
    {
        _nodeStore = nodeStore;
        _protector = protector;
        _clientFactory = clientFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DatabaseOverview> GetOverviewAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var nodes = await ResolveNodesAsync(nodeId is { } id ? new NodeId(id) : null, cancellationToken)
            .ConfigureAwait(false);

        var calls = await CallEachNodeAsync(nodes, LoadNodeAsync, cancellationToken).ConfigureAwait(false);
        var groups = calls
            .Select(call => call.Value ?? new DatabaseNodeGroup(
                call.Node.Id.Value,
                call.Node.Name,
                call.Node.IsEnabled,
                [],
                [],
                call.Error,
                call.Elapsed))
            .ToArray();

        return new DatabaseOverview(groups, _timeProvider.GetUtcNow());
    }

    public async Task<DatabaseReadResult<DatabaseUserView>> GetUsersAsync(
        Guid nodeId,
        string instance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));
        var result = await client
            .PostAsync<List<DatabaseUserDto>>(
                DatabaseRoutes.UsersSearch,
                new DatabaseUserSearchRequest(instance),
                timeoutCts.Token)
            .ConfigureAwait(false);

        var users = (result.Data ?? [])
            .Where(user => !string.IsNullOrWhiteSpace(user.Username))
            .Select(user => new DatabaseUserView(
                user.Username!.Trim(),
                string.IsNullOrWhiteSpace(user.Host) ? "%" : user.Host.Trim(),
                string.IsNullOrWhiteSpace(user.Description) ? null : user.Description.Trim()))
            .ToArray();

        return new DatabaseReadResult<DatabaseUserView>(users, result.Error);
    }

    public async Task<DatabaseReadResult<DatabaseGrantView>> GetGrantsAsync(
        Guid nodeId,
        string instance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));
        var result = await client
            .PostAsync<List<DatabaseGrantDto>>(
                DatabaseRoutes.GrantsSearch,
                new DatabaseUserSearchRequest(instance),
                timeoutCts.Token)
            .ConfigureAwait(false);

        var grants = (result.Data ?? [])
            .Where(grant => !string.IsNullOrWhiteSpace(grant.Database) && !string.IsNullOrWhiteSpace(grant.Username))
            .Select(grant => new DatabaseGrantView(
                grant.Database!.Trim(),
                grant.Username!.Trim(),
                string.IsNullOrWhiteSpace(grant.Host) ? "%" : grant.Host.Trim()))
            .ToArray();

        return new DatabaseReadResult<DatabaseGrantView>(grants, result.Error);
    }

    public async Task<DatabaseReadResult<DatabaseFormatView>> GetFormatsAsync(
        Guid nodeId,
        string instance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));
        var result = await client
            .PostAsync<List<DatabaseFormatOptionDto>>(
                DatabaseRoutes.FormatOptions,
                new { name = instance },
                timeoutCts.Token)
            .ConfigureAwait(false);

        var formats = (result.Data ?? [])
            .Where(option => !string.IsNullOrWhiteSpace(option.Format))
            .Select(option => new DatabaseFormatView(
                option.Format!.Trim(),
                option.Collations?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? []))
            .ToArray();

        return new DatabaseReadResult<DatabaseFormatView>(formats, result.Error);
    }

    public async Task<DatabaseReadResult<DatabaseLinkResourceView>> CheckDeleteAsync(
        Guid nodeId,
        DatabaseEngine engine,
        long databaseId,
        string instance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        if (!DatabaseEngines.IsDatabaseEngine(engine))
        {
            throw new DatabaseOperationRejectedException("该引擎不支持库删除检查。");
        }

        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));
        var result = await client
            .PostAsync<List<DatabaseLinkedResourceDto>>(
                DatabaseRoutes.DeleteCheck(DatabaseEngines.Key(engine)),
                new DatabaseDeleteCheckRequest(databaseId, DatabaseEngines.Key(engine), instance),
                timeoutCts.Token)
            .ConfigureAwait(false);

        var resources = (result.Data ?? [])
            .Where(resource => !string.IsNullOrWhiteSpace(resource.Name))
            .Select(resource => new DatabaseLinkResourceView(
                string.IsNullOrWhiteSpace(resource.Type) ? "unknown" : resource.Type!.Trim(),
                resource.Name!.Trim()))
            .ToArray();

        return new DatabaseReadResult<DatabaseLinkResourceView>(resources, result.Error);
    }

    public async Task<DatabaseStatusView> GetStatusAsync(
        Guid nodeId,
        DatabaseEngine engine,
        string instance,
        string from,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        if (!DatabaseEngines.IsDatabaseEngine(engine))
        {
            return UnsupportedStatus("Redis 引擎不支持关系型数据库状态监控。");
        }

        if (!string.Equals(from, "local", StringComparison.OrdinalIgnoreCase))
        {
            return UnsupportedStatus("远程实例不支持状态/变量监控（1Panel 仅对本地或应用安装实例提供）。");
        }

        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));
        var result = await client
            .PostAsync<DatabaseStatusDto>(
                DatabaseRoutes.Status,
                new { type = DatabaseEngines.Key(engine), name = instance },
                timeoutCts.Token)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            return UnsupportedStatus("1Panel 未返回实例状态（可能为远程实例或版本不支持）。") with
            {
                Error = result.Error
            };
        }

        var data = result.Data;
        return new DatabaseStatusView(
            true,
            null,
            data?.ThreadsConnected,
            data?.ThreadsRunning,
            data?.MaxUsedConnections,
            data?.Uptime,
            data?.Questions,
            data?.BytesReceived,
            data?.BytesSent,
            data?.SlowQueries,
            null);
    }

    public async Task<DatabaseRedisView> GetRedisInfoAsync(
        Guid nodeId,
        string instance,
        string from,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);
        var unsupportedReason = string.Equals(from, "local", StringComparison.OrdinalIgnoreCase)
            ? null
            : "远程实例不支持 Redis 状态/配置读取（1Panel 仅对本地或应用安装实例提供）。";

        using var timeoutCts = CreateNodeTimeout(cancellationToken);
        using var client = _clientFactory.Create(BuildDescriptor(node));

        if (unsupportedReason is not null)
        {
            return new DatabaseRedisView(
                node.Id.Value,
                node.Name,
                instance,
                from,
                CliAvailable: false,
                Supported: false,
                unsupportedReason,
                null,
                null,
                null);
        }

        var cli = await client.GetAsync<bool>(DatabaseRoutes.RedisCliCheck, timeoutCts.Token).ConfigureAwait(false);
        var cliAvailable = cli.Error is null && cli.Data;
        if (!cliAvailable)
        {
            return new DatabaseRedisView(
                node.Id.Value,
                node.Name,
                instance,
                from,
                CliAvailable: false,
                Supported: false,
                "节点未安装 Redis CLI，无法读取状态与配置。",
                null,
                null,
                cli.Error);
        }

        var body = new { type = "redis", name = instance };
        var status = await client
            .PostAsync<RedisStatusDto>(DatabaseRoutes.RedisStatus, body, timeoutCts.Token)
            .ConfigureAwait(false);
        var conf = await client
            .PostAsync<RedisConfDto>(DatabaseRoutes.RedisConf, body, timeoutCts.Token)
            .ConfigureAwait(false);
        var persistence = await client
            .PostAsync<RedisPersistenceDto>(DatabaseRoutes.RedisPersistenceConf, body, timeoutCts.Token)
            .ConfigureAwait(false);

        var statusData = status.Data;
        var statusView = statusData is null
            ? null
            : new DatabaseRedisStatusView(
                statusData.TcpPort,
                statusData.UptimeInDays,
                statusData.ConnectedClients,
                statusData.UsedMemory,
                statusData.UsedMemoryRss,
                statusData.UsedMemoryPeak,
                statusData.MemFragmentationRatio,
                statusData.TotalConnectionsReceived,
                statusData.TotalCommandsProcessed,
                statusData.InstantaneousOpsPerSec);

        var confView = conf.Data is null && persistence.Data is null
            ? null
            : new DatabaseRedisConfView(
                conf.Data?.Timeout,
                conf.Data?.Maxclients,
                conf.Data?.Maxmemory,
                persistence.Data?.Appendonly,
                persistence.Data?.Appendfsync,
                persistence.Data?.Save);

        return new DatabaseRedisView(
            node.Id.Value,
            node.Name,
            instance,
            from,
            cliAvailable,
            Supported: true,
            null,
            statusView,
            confView,
            status.Error ?? conf.Error ?? persistence.Error);
    }

    private async Task<DatabaseNodeGroup> LoadNodeAsync(
        NodeRecord node,
        IPanelClient client,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var instances = new List<DatabaseInstanceView>();
        var warnings = new List<string>();
        var failedEngines = 0;
        var successfulEngines = 0;

        foreach (var engine in DatabaseEngines.All)
        {
            var engineKey = DatabaseEngines.Key(engine);
            var display = DatabaseEngines.DisplayName(engine);
            var list = await client
                .GetAsync<List<DatabaseInstanceDto>>(DatabaseRoutes.Instances(engineKey), cancellationToken)
                .ConfigureAwait(false);

            if (list.Error is not null)
            {
                if (list.Error.Kind is PanelErrorKind.NotFound or PanelErrorKind.Unsupported)
                {
                    continue;
                }

                failedEngines++;
                warnings.Add($"{display} 实例列表失败：{list.Error.Message}");
                continue;
            }

            successfulEngines++;
            var engineInstances = (list.Data ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Database))
                .ToArray();

            foreach (var item in engineInstances)
            {
                var instanceName = item.Database!.Trim();
                if (!DatabaseEngines.IsDatabaseEngine(engine))
                {
                    instances.Add(new DatabaseInstanceView(
                        node.Id.Value,
                        node.Name,
                        engine,
                        instanceName,
                        item.Id,
                        item.From ?? string.Empty,
                        item.Version ?? string.Empty,
                        item.Address ?? string.Empty,
                        [],
                        0,
                        false,
                        null));
                    continue;
                }

                var search = await client
                    .PostAsync<PagedItemsDto<DatabaseDbDto>>(
                        DatabaseRoutes.Search(engineKey),
                        new DatabaseSearchRequest(
                            1,
                            _options.PerInstancePageSize,
                            string.Empty,
                            instanceName,
                            "createdAt",
                            "descending"),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (search.Error is not null)
                {
                    warnings.Add($"{display}/{instanceName} 库列表失败：{search.Error.Message}");
                    instances.Add(new DatabaseInstanceView(
                        node.Id.Value,
                        node.Name,
                        engine,
                        instanceName,
                        item.Id,
                        item.From ?? string.Empty,
                        item.Version ?? string.Empty,
                        item.Address ?? string.Empty,
                        [],
                        0,
                        false,
                        search.Error));
                    continue;
                }

                var rows = MapDatabases(node, engine, instanceName, item, search.Data?.Items ?? []);
                var total = ToInt(search.Data?.Total ?? rows.Length);
                instances.Add(new DatabaseInstanceView(
                    node.Id.Value,
                    node.Name,
                    engine,
                    instanceName,
                    item.Id,
                    item.From ?? string.Empty,
                    item.Version ?? string.Empty,
                    item.Address ?? string.Empty,
                    rows,
                    total,
                    total > rows.Length,
                    null));
            }
        }

        PanelError? nodeError = successfulEngines == 0 && failedEngines > 0
            ? new PanelError(PanelErrorKind.PanelServerError, $"[{node.Name}] 数据库引擎列表均读取失败")
            : null;

        return new DatabaseNodeGroup(
            node.Id.Value,
            node.Name,
            node.IsEnabled,
            instances,
            warnings,
            nodeError,
            stopwatch.Elapsed);
    }

    private DatabaseDbView[] MapDatabases(
        NodeRecord node,
        DatabaseEngine engine,
        string instanceName,
        DatabaseInstanceDto instance,
        IReadOnlyList<DatabaseDbDto> items) =>
        items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new DatabaseDbView(
                node.Id.Value,
                node.Name,
                engine,
                instanceName,
                item.Id,
                item.Name!.Trim(),
                item.From ?? instance.From ?? string.Empty,
                NullIfEmpty(item.Format),
                NullIfEmpty(item.Collation),
                NullIfEmpty(item.Description),
                item.SuperUser ?? false,
                item.CreatedAt))
            .ToArray();

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
            throw new DatabaseNodeNotFoundException(nodeId.Value);
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
                    "数据库只读请求异常 节点 {NodeId} 类型 {Type}",
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

    private static DatabaseStatusView UnsupportedStatus(string reason) =>
        new(false, reason, null, null, null, null, null, null, null, null, null);

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

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed record NodeCallResult<T>(
        NodeRecord Node,
        T? Value,
        PanelError? Error,
        TimeSpan Elapsed);
}

