using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.AppStore;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.AppStore;

/// <summary>应用商店单元测试用假面板调用记录。</summary>
internal sealed record RecordedPanelCall(string Method, string Path, string? Body, string NodeName);

/// <summary>
/// 应用商店单元测试用假 1Panel 客户端工厂：按路径包含关键字返回预设 JSON，并记录所有调用（含请求体）。
/// </summary>
internal sealed class RecordingPanelClientFactory : IPanelClientFactory
{
    private readonly ConcurrentDictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PanelErrorKind> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int AllowedCalls, PanelErrorKind Kind)> _failAfterCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _matchedCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecordedPanelCall> _calls = [];

    public IReadOnlyList<RecordedPanelCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>按路径“包含”匹配返回 data JSON；路径最长匹配优先。</summary>
    public RecordingPanelClientFactory Respond(string pathContains, string dataJson)
    {
        _responses[pathContains] = dataJson;
        return this;
    }

    /// <summary>按路径“包含”匹配返回错误。</summary>
    public RecordingPanelClientFactory Fail(string pathContains, PanelErrorKind kind)
    {
        _failures[pathContains] = kind;
        return this;
    }

    /// <summary>按路径“包含”匹配：前 <paramref name="allowedCalls"/> 次成功，之后返回错误（用于验证部分成功/失败停止）。</summary>
    public RecordingPanelClientFactory FailAfterCalls(string pathContains, int allowedCalls, PanelErrorKind kind)
    {
        _failAfterCalls[pathContains] = (allowedCalls, kind);
        return this;
    }

    public IPanelClient Create(NodeDescriptor node) => new RecordingPanelClient(this, node);

    internal PanelCallResult<T> Handle<T>(NodeDescriptor node, string method, string path, object? body)
    {
        var bodyText = body is null ? null : JsonSerializer.Serialize(body);
        lock (_calls)
        {
            _calls.Add(new RecordedPanelCall(method, path, bodyText, node.Name));
        }

        foreach (var failure in _failures)
        {
            if (path.Contains(failure.Key, StringComparison.OrdinalIgnoreCase))
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(failure.Value, $"[{node.Name}] 假面板错误：{failure.Key}"),
                    TimeSpan.FromMilliseconds(3));
            }
        }

        foreach (var rule in _failAfterCalls)
        {
            if (!path.Contains(rule.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matched = _matchedCalls.AddOrUpdate(rule.Key, 1, (_, count) => count + 1);
            if (matched > rule.Value.AllowedCalls)
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(rule.Value.Kind, $"[{node.Name}] 假面板第 {matched} 次调用失败：{rule.Key}"),
                    TimeSpan.FromMilliseconds(3));
            }
        }

        var match = _responses
            .Where(pair => path.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();

        if (match.Key is null)
        {
            return new PanelCallResult<T>(
                default,
                new PanelError(PanelErrorKind.NotFound, $"[{node.Name}] 未配置假响应：{path}"),
                TimeSpan.FromMilliseconds(3));
        }

        if (string.Equals(match.Value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return new PanelCallResult<T>(default, null, TimeSpan.FromMilliseconds(3));
        }

        var data = JsonSerializer.Deserialize<T>(match.Value, TestJson.Options);
        return new PanelCallResult<T>(data, null, TimeSpan.FromMilliseconds(3));
    }

    private sealed class RecordingPanelClient : IPanelClient
    {
        private readonly RecordingPanelClientFactory _factory;

        public RecordingPanelClient(RecordingPanelClientFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node, "GET", relativePath, null));

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node, "POST", relativePath, body));

        public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<OsInfoDto>(null, null, TimeSpan.Zero));

        public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<DashboardCurrentDto>(null, null, TimeSpan.Zero));

        public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
            string ioOption = "all",
            string netOption = "all",
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<DashboardBaseDto>(null, null, TimeSpan.Zero));

        public void Dispose()
        {
        }
    }
}

/// <summary>应用商店单元测试用假节点存储。</summary>
internal sealed class AppStoreFakeNodeStore : INodeStore
{
    public AppStoreFakeNodeStore(params NodeRecord[] nodes) => Nodes = nodes.ToList();

    public List<NodeRecord> Nodes { get; }

    public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeRecord>>(Nodes);

    public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeId>>(Nodes.Where(node => node.IsEnabled).Select(node => node.Id).ToArray());

    public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
        Task.FromResult(Nodes.FirstOrDefault(node => node.Id == id));

    public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task AddAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class AppStoreFakeProtector : INodeSecretProtector
{
    public ProtectedSecret Protect(string apiKey) => new("cipher", "hint");

    public string Unprotect(string cipherText) => "test-api-key";
}

internal static class TestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}

/// <summary>应用商店单元测试数据构造。</summary>
internal static class AppStoreTestData
{
    public static NodeRecord Node(string name = "node-a", bool enabled = true) => new(
        NodeId.New(),
        name,
        new Uri("https://" + name + ".example.test/"),
        null,
        "cipher",
        "hint",
        AuthMode.HmacSha256,
        null,
        null,
        TlsPolicy.Strict,
        null,
        [],
        null,
        enabled,
        "v2.3.2",
        0,
        NodeHealth.Online,
        null,
        null,
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    public static JsonObject InstallParameters(
        long appDetailId = 2064,
        string name = "mysql",
        string? version = "8.4.13",
        string? appName = "MySQL",
        bool pullImage = true)
    {
        var parameters = new JsonObject
        {
            ["appDetailId"] = appDetailId,
            ["name"] = name,
            ["appKey"] = "mysql",
            ["appName"] = appName,
            ["params"] = new JsonObject
            {
                ["PANEL_APP_PORT_HTTP"] = 3306,
                ["PANEL_DB_ROOT_PASSWORD"] = "secret-value"
            },
            ["pullImage"] = pullImage
        };

        if (version is not null)
        {
            parameters["version"] = version;
        }

        return parameters;
    }

    public static OperationPlanContext PlanContext(NodeRecord[] nodes, JsonObject? parameters, string kind = "app.install") =>
        new(
            new OperationInvocation(kind, nodes.Select(node => node.Id).ToArray(), parameters),
            nodes,
            OperationParameters.From(parameters));

    public static OperationNodeContext NodeContext(NodeRecord node, JsonObject? parameters, IPanelClient client) =>
        new(Guid.NewGuid(), node, client, OperationParameters.From(parameters));

    public static NodeDescriptor Descriptor(NodeRecord node) => new(
        node.Id,
        node.Name,
        node.BaseUrl,
        "test-api-key",
        node.AuthMode,
        node.TlsPolicy);

    public static ActorInfo Actor() => new("user-1", "Alice", "alice@example.test", "127.0.0.1", "trace-1");
}
