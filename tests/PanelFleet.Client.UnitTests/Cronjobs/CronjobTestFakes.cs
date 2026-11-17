using System.Collections.Concurrent;
using System.Text.Json;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Cronjobs;

internal static class CronjobTestData
{
    public static NodeRecord Node(string name, bool enabled = true)
    {
        var now = DateTimeOffset.UtcNow;
        return new NodeRecord(
            NodeId.New(),
            name,
            new Uri("https://" + name + ".example.test"),
            null,
            "protected-api-key",
            "hint",
            AuthMode.Auto,
            AuthMode.Md5,
            0,
            TlsPolicy.Strict,
            null,
            [],
            null,
            enabled,
            "v2.3.2",
            0,
            NodeHealth.Online,
            null,
            now,
            now,
            now,
            now);
    }
}

internal sealed class CronjobNodeStore : INodeStore
{
    private readonly List<NodeRecord> _nodes;

    public CronjobNodeStore(params NodeRecord[] nodes) => _nodes = nodes.ToList();

    public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeRecord>>(_nodes);

    public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeId>>(_nodes.Where(node => node.IsEnabled).Select(node => node.Id).ToArray());

    public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
        Task.FromResult(_nodes.FirstOrDefault(node => node.Id == id));

    public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(_nodes.Any(node => node.Name == name && node.Id != exceptId));

    public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(_nodes.Any(node => node.BaseUrl == baseUrl && node.Id != exceptId));

    public Task AddAsync(NodeRecord node, CancellationToken cancellationToken)
    {
        _nodes.Add(node);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CronjobSecretProtector : INodeSecretProtector
{
    public ProtectedSecret Protect(string apiKey) => new("protected:" + apiKey, "hint");

    public string Unprotect(string cipherText) => cipherText.Replace("protected:", string.Empty, StringComparison.Ordinal);
}

internal sealed record CronjobCall(string NodeName, string Path, string? Method, string? Body);

/// <summary>精确路径匹配的假 1Panel 工厂（避免 contains 匹配到相邻路由）。</summary>
internal sealed class CronjobPanelClientFactory : IPanelClientFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, string> _payloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<PanelError>> _failNext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int CallNumber, PanelError Error)> _failAtCall = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _callCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PanelError> _nodeFailures = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<CronjobCall> Calls { get; } = new();

    public CronjobPanelClientFactory Map(string path, string json)
    {
        _payloads[path] = json;
        return this;
    }

    public CronjobPanelClientFactory Fail(string nodeName, PanelError error)
    {
        _nodeFailures[nodeName] = error;
        return this;
    }

    /// <summary>下一次请求该路径时返回错误（可用于「批量第 N 个失败」）。</summary>
    public CronjobPanelClientFactory FailNext(string path, PanelError error)
    {
        if (!_failNext.TryGetValue(path, out var queue))
        {
            queue = new Queue<PanelError>();
            _failNext[path] = queue;
        }

        queue.Enqueue(error);
        return this;
    }

    /// <summary>第 N 次请求该路径时返回错误（1 基），用于验证「批量中途失败停止」。</summary>
    public CronjobPanelClientFactory FailAtCall(string path, int callNumber, PanelError error)
    {
        _failAtCall[path] = (callNumber, error);
        return this;
    }

    public IPanelClient Create(NodeDescriptor node) => new CronjobFakePanelClient(this, node);

    internal PanelCallResult<T> Handle<T>(
        string nodeName,
        string relativePath,
        string method,
        object? body,
        CancellationToken cancellationToken)
    {
        Calls.Enqueue(new CronjobCall(
            nodeName,
            relativePath,
            method,
            body is null ? null : JsonSerializer.Serialize(body, JsonOptions)));

        var callCount = _callCounts.AddOrUpdate(relativePath, 1, (_, current) => current + 1);

        if (_nodeFailures.TryGetValue(nodeName, out var nodeError))
        {
            return new PanelCallResult<T>(default, nodeError, TimeSpan.FromMilliseconds(1));
        }

        if (_failAtCall.TryGetValue(relativePath, out var scheduled) && callCount == scheduled.CallNumber)
        {
            return new PanelCallResult<T>(default, scheduled.Error, TimeSpan.FromMilliseconds(1));
        }

        if (_failNext.TryGetValue(relativePath, out var queue) && queue.Count > 0)
        {
            return new PanelCallResult<T>(default, queue.Dequeue(), TimeSpan.FromMilliseconds(1));
        }

        if (_payloads.TryGetValue(relativePath, out var json))
        {
            return new PanelCallResult<T>(
                JsonSerializer.Deserialize<T>(json, JsonOptions),
                null,
                TimeSpan.FromMilliseconds(1));
        }

        return new PanelCallResult<T>(
            default,
            new PanelError(PanelErrorKind.NotFound, "[" + nodeName + "] 假客户端未实现路径：" + relativePath),
            TimeSpan.FromMilliseconds(1));
    }

    public IReadOnlyList<CronjobCall> CallsFor(string path) =>
        Calls.Where(call => call.Path == path).ToArray();

    private sealed class CronjobFakePanelClient : IPanelClient
    {
        private readonly CronjobPanelClientFactory _factory;

        public CronjobFakePanelClient(CronjobPanelClientFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.Md5, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node.Name, relativePath, "GET", null, cancellationToken));

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node.Name, relativePath, "POST", body, cancellationToken));

        public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
            string ioOption = "all",
            string netOption = "all",
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
