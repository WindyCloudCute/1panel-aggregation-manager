using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Overview;
using PanelFleet.Application.Resources;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;
using PanelFleet.Client.UnitTests.Overview;

namespace PanelFleet.Client.UnitTests.Resources;

public sealed class ResourceQueryServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task QueryAsync_MergesNodes_AndFilters()
    {
        var (service, _) = CreateService(
            new[] { "node-a", "node-b" },
            new ResourceOptions { MaxConcurrency = 2, NodeTimeoutSeconds = 5, BudgetSeconds = 10 });

        var page = await service.QueryAsync(
            ResourceKind.Apps,
            new ResourceQuery("app-1", "name", false, 1, 50, null),
            ResourceQueryMode.EnsureFresh,
            CancellationToken.None);

        Assert.Equal(2, page.TotalMatched);
        Assert.All(page.Rows, row => Assert.Equal("app-1", row.Name));
        Assert.Equal(2, page.NodesSucceeded);
        Assert.Equal(0, page.NodesFailed);
        Assert.Equal(2, page.NodesTotal);
    }

    [Fact]
    public async Task QueryAsync_PartialFailure_KeepsOtherNodes()
    {
        var (service, factory) = CreateService(
            new[] { "node-a", "bad-node" },
            new ResourceOptions { MaxConcurrency = 2, NodeTimeoutSeconds = 5, BudgetSeconds = 10 });

        factory.SetFailure("bad-node", PanelErrorKind.Unauthorized);

        var page = await service.QueryAsync(
            ResourceKind.Apps,
            new ResourceQuery(null, "name", false, 1, 50, null),
            ResourceQueryMode.EnsureFresh,
            CancellationToken.None);

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(1, page.NodesSucceeded);
        Assert.Equal(1, page.NodesFailed);
        var bad = page.Nodes.Single(node => node.NodeName == "bad-node");
        Assert.Equal(ResourceNodeState.Failed, bad.State);
        Assert.Equal(PanelErrorKind.Unauthorized, bad.Error?.Kind);
    }

    [Fact]
    public async Task QueryAsync_Cache_AvoidsRepeatedCalls()
    {
        var (service, factory) = CreateService(
            new[] { "node-a", "node-b" },
            new ResourceOptions { CacheTtlSeconds = 60, MaxConcurrency = 2, NodeTimeoutSeconds = 5, BudgetSeconds = 10 });

        await service.QueryAsync(
            ResourceKind.Apps,
            new ResourceQuery(null, null, false, 1, 50, null),
            ResourceQueryMode.EnsureFresh,
            CancellationToken.None);

        await service.QueryAsync(
            ResourceKind.Apps,
            new ResourceQuery(null, null, false, 1, 50, null),
            ResourceQueryMode.EnsureFresh,
            CancellationToken.None);

        await service.QueryAsync(
            ResourceKind.Apps,
            new ResourceQuery(null, null, false, 1, 50, null),
            ResourceQueryMode.CacheOnly,
            CancellationToken.None);

        Assert.Equal(1, factory.Calls("node-a"));
        Assert.Equal(1, factory.Calls("node-b"));
    }

    [Fact]
    public async Task QueryAsync_TruncatesPerNodeLimit()
    {
        var (service, _) = CreateService(
            new[] { "node-a" },
            new ResourceOptions { PerNodeLimit = 1, MaxConcurrency = 1, NodeTimeoutSeconds = 5, BudgetSeconds = 10 });

        var page = await service.QueryAsync(
            ResourceKind.Apps,
            new ResourceQuery(null, null, false, 1, 50, null),
            ResourceQueryMode.EnsureFresh,
            CancellationToken.None);

        Assert.Single(page.Rows);
        Assert.True(page.Nodes.Single().Truncated);
        Assert.Equal(2, page.Nodes.Single().Total);
    }

    [Fact]
    public async Task RefreshAsync_ForcesReload()
    {
        var (service, factory) = CreateService(
            new[] { "node-a" },
            new ResourceOptions { CacheTtlSeconds = 60, MaxConcurrency = 1, NodeTimeoutSeconds = 5, BudgetSeconds = 10 });

        var query = new ResourceQuery(null, null, false, 1, 50, null);
        await service.QueryAsync(ResourceKind.Apps, query, ResourceQueryMode.EnsureFresh, CancellationToken.None);
        await service.RefreshAsync(ResourceKind.Apps, query, CancellationToken.None);

        Assert.Equal(2, factory.Calls("node-a"));
    }

    [Fact]
    public async Task QueryAsync_Databases_UsesInstancesThenRows()
    {
        var (service, factory) = CreateService(
            new[] { "node-a" },
            new ResourceOptions { MaxConcurrency = 1, NodeTimeoutSeconds = 5, BudgetSeconds = 10 });

        var page = await service.QueryAsync(
            ResourceKind.Databases,
            new ResourceQuery(null, null, false, 1, 50, null),
            ResourceQueryMode.EnsureFresh,
            CancellationToken.None);

        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, row => Assert.Equal("MySQL", row.Attributes["engine"]));
        Assert.All(page.Rows, row => Assert.Equal("MySQL", row.Attributes["instance"]));
        Assert.True(factory.Calls("node-a") >= 3); // 3 个引擎实例列表 + MySQL 库列表
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsFlattenedFields()
    {
        var (service, _) = CreateService(
            new[] { "node-a" },
            new ResourceOptions { MaxConcurrency = 1, NodeTimeoutSeconds = 5, BudgetSeconds = 10 });

        var page = await service.QueryAsync(
            ResourceKind.Apps,
            new ResourceQuery(null, null, false, 1, 50, null),
            ResourceQueryMode.EnsureFresh,
            CancellationToken.None);
        var nodeId = page.Nodes[0].NodeId;

        var detail = await service.GetDetailAsync(ResourceKind.Apps, nodeId, "1", CancellationToken.None);

        Assert.Null(detail.Error);
        Assert.Equal("app-1", detail.Fields["name"]);
        Assert.Equal("8080", detail.Fields["HttpPort"]);
    }

    private static (ResourceQueryService Service, FakePanelClientFactory Factory) CreateService(
        IReadOnlyList<string> nodeNames,
        ResourceOptions options)
    {
        var nodes = nodeNames.Select(name => TestNodes.Record(name: name, enabled: true)).ToArray();
        var store = new FakeNodeStore(nodes);
        var factory = new FakePanelClientFactory();
        var loaders = new IResourceLoader[]
        {
            new WebsiteResourceLoader(),
            new AppResourceLoader(),
            new ContainerResourceLoader(),
            new DatabaseResourceLoader(),
            new CronjobResourceLoader()
        };

        var service = new ResourceQueryService(
            store,
            new FakeProtector(),
            factory,
            new FanoutEngine(TimeProvider.System, NullLogger<FanoutEngine>.Instance),
            loaders,
            new ResourceCache(),
            new ResourceAccessTracker(options),
            new ResourceGates(),
            options,
            TimeProvider.System,
            NullLogger<ResourceQueryService>.Instance);

        return (service, factory);
    }

    private sealed class FakeNodeStore : INodeStore
    {
        private readonly IReadOnlyList<NodeRecord> _nodes;

        public FakeNodeStore(IReadOnlyList<NodeRecord> nodes)
        {
            _nodes = nodes;
        }

        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(_nodes);

        public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NodeId>>(_nodes.Where(node => node.IsEnabled).Select(node => node.Id).ToArray());

        public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
            Task.FromResult(_nodes.FirstOrDefault(node => node.Id == id));

        public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task AddAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeProtector : INodeSecretProtector
    {
        public ProtectedSecret Protect(string apiKey) => new(apiKey, apiKey.Length >= 4 ? apiKey[^4..] : "••••");

        public string Unprotect(string cipherText) => cipherText;
    }

    private sealed class FakePanelClientFactory : IPanelClientFactory
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PanelErrorKind> _failures = new(StringComparer.OrdinalIgnoreCase);

        public void SetFailure(string nodeName, PanelErrorKind kind) => _failures[nodeName] = kind;

        public int Calls(string nodeName) => _calls.TryGetValue(nodeName, out var count) ? count : 0;

        public IPanelClient Create(NodeDescriptor node)
        {
            _failures.TryGetValue(node.Name, out var failure);
            var hasFailure = _failures.ContainsKey(node.Name);
            return new FakePanelClient(node, hasFailure ? failure : null, () =>
                _calls.AddOrUpdate(node.Name, 1, static (_, count) => count + 1));
        }
    }

    private sealed class FakePanelClient : IPanelClient
    {
        private readonly PanelErrorKind? _failure;
        private readonly Action _onCall;

        public FakePanelClient(NodeDescriptor node, PanelErrorKind? failure, Action onCall)
        {
            Node = node;
            _failure = failure;
            _onCall = onCall;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
            Unsupported<OsInfoDto>();

        public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(CancellationToken cancellationToken = default) =>
            Unsupported<DashboardCurrentDto>();

        public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
            string ioOption = "all",
            string netOption = "all",
            CancellationToken cancellationToken = default) => Unsupported<DashboardBaseDto>();

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            HandleAsync<T>(relativePath);

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) => HandleAsync<T>(relativePath);

        public void Dispose()
        {
        }

        private Task<PanelCallResult<T>> HandleAsync<T>(string path)
        {
            _onCall();

            if (_failure is { } failure)
            {
                return Task.FromResult(new PanelCallResult<T>(
                    default,
                    new PanelError(failure, $"[{Node.Name}] fake failure"),
                    TimeSpan.FromMilliseconds(5)));
            }

            var json = ResourceJson(path);
            if (json is null)
            {
                return Task.FromResult(new PanelCallResult<T>(
                    default,
                    new PanelError(PanelErrorKind.NotFound, "not found"),
                    TimeSpan.FromMilliseconds(5)));
            }

            var data = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return Task.FromResult(new PanelCallResult<T>(data, null, TimeSpan.FromMilliseconds(5)));
        }

        private static Task<PanelCallResult<T>> Unsupported<T>() =>
            Task.FromResult(new PanelCallResult<T>(
                default,
                new PanelError(PanelErrorKind.Unsupported, "unsupported"),
                TimeSpan.Zero));

        private static string? ResourceJson(string path)
        {
            if (path.Contains("apps/installed/info", StringComparison.Ordinal))
            {
                return """{"id":1,"name":"app-1","version":"1.0","status":"Running","HttpPort":8080,"appKey":"app-1"}""";
            }

            if (path.Contains("apps/installed/search", StringComparison.Ordinal))
            {
                return """{"total":2,"items":[{"id":1,"name":"app-1","version":"1.0","status":"Running","httpPort":8080},{"id":2,"name":"app-2","version":"2.0","status":"Stopped","httpPort":8081}]}""";
            }

            if (path.Contains("websites/search", StringComparison.Ordinal))
            {
                return """{"total":1,"items":[{"id":1,"primaryDomain":"example.com","type":"static","status":"Running"}]}""";
            }

            if (path.Contains("containers/search", StringComparison.Ordinal))
            {
                return """{"total":1,"items":[{"containerID":"abc","name":"web","imageName":"nginx:latest","state":"running","runTime":"Up 2 hours"}]}""";
            }

            if (path.Contains("cronjobs/search", StringComparison.Ordinal))
            {
                return """{"total":1,"items":[{"id":1,"name":"backup","type":"shell","spec":"0 1 * * *","status":"Enable","retainCopies":7}]}""";
            }

            if (path.Contains("databases/db/list/mysql", StringComparison.Ordinal))
            {
                return """[{"id":1,"type":"mysql","from":"remote","database":"MySQL","version":"8.x","address":"198.51.100.11"}]""";
            }

            if (path.Contains("databases/db/list/", StringComparison.Ordinal))
            {
                return "null";
            }

            if (path.Contains("databases/search", StringComparison.Ordinal))
            {
                return """{"total":2,"items":[{"id":1,"name":"db_one","mysqlName":"MySQL","from":"remote","format":"utf8mb4","collation":"utf8mb4_general_ci"},{"id":2,"name":"db_two","mysqlName":"MySQL","from":"remote","format":"utf8mb4","collation":"utf8mb4_general_ci"}]}""";
            }

            return null;
        }
    }
}
