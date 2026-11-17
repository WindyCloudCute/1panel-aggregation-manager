using System.Collections.Concurrent;
using System.Text.Json;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Containers;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Containers;

internal static class ContainerTestData
{
    public static NodeRecord Node(string name, bool enabled = true) => new(
        new NodeId(Guid.NewGuid()),
        name,
        new Uri("https://" + name + ".example.test"),
        null,
        "protected-api-key",
        "hint",
        AuthMode.Auto,
        null,
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
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}

internal sealed class FakeNodeStore : INodeStore
{
    private readonly List<NodeRecord> _nodes;

    public FakeNodeStore(IEnumerable<NodeRecord> nodes) => _nodes = nodes.ToList();

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

    public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken)
    {
        var index = _nodes.FindIndex(item => item.Id == node.Id);
        if (index >= 0)
        {
            _nodes[index] = node;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(NodeId id, CancellationToken cancellationToken)
    {
        _nodes.RemoveAll(node => node.Id == id);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSecretProtector : INodeSecretProtector
{
    public ProtectedSecret Protect(string apiKey) => new("protected:" + apiKey, "hint");

    public string Unprotect(string cipherText) => cipherText.Replace("protected:", string.Empty, StringComparison.Ordinal);
}

internal sealed record FakeCall(string NodeName, string Path, string? Method, string? Body);

/// <summary>按路径子串返回 JSON 的假 1Panel 工厂；可按节点注入失败。</summary>
internal sealed class FakePanelClientFactory : IPanelClientFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, string> _payloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PanelError> _failures = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<FakeCall> Calls { get; } = new();

    public FakePanelClientFactory Map(string pathContains, string json)
    {
        _payloads[pathContains] = json;
        return this;
    }

    public FakePanelClientFactory Fail(string nodeName, PanelError error)
    {
        _failures[nodeName] = error;
        return this;
    }

    public IPanelClient Create(NodeDescriptor node) => new FakePanelClient(this, node);

    internal PanelCallResult<T> Handle<T>(string nodeName, string relativePath, string method, string? body, TimeSpan elapsed)
    {
        Calls.Enqueue(new FakeCall(nodeName, relativePath, method, body));

        if (_failures.TryGetValue(nodeName, out var error))
        {
            return new PanelCallResult<T>(default, error, elapsed);
        }

        foreach (var pair in _payloads)
        {
            if (relativePath.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return new PanelCallResult<T>(
                    JsonSerializer.Deserialize<T>(pair.Value, JsonOptions),
                    null,
                    elapsed);
            }
        }

        return new PanelCallResult<T>(
            default,
            new PanelError(PanelErrorKind.NotFound, "[" + nodeName + "] 假客户端未实现路径：" + relativePath),
            elapsed);
    }

    private sealed class FakePanelClient : IPanelClient
    {
        private readonly FakePanelClientFactory _factory;

        public FakePanelClient(FakePanelClientFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node.Name, relativePath, "GET", null, TimeSpan.FromMilliseconds(1)));

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(
                Node.Name,
                relativePath,
                "POST",
                body is null ? null : JsonSerializer.Serialize(body, JsonOptions),
                TimeSpan.FromMilliseconds(1)));

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

internal sealed class FakeContainerLogClientFactory : IContainerLogClientFactory
{
    public IReadOnlyList<string> Lines { get; set; } = ["line-1", "line-2"];

    public PanelError? Error { get; set; }

    public ContainerLogQuery? LastQuery { get; private set; }

    public IContainerLogClient Create(NodeDescriptor node) => new FakeLogClient(this, node);

    private sealed class FakeLogClient : IContainerLogClient
    {
        private readonly FakeContainerLogClientFactory _factory;

        public FakeLogClient(FakeContainerLogClientFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            NodeName = node.Name;
        }

        public string NodeName { get; }

        public Task<ContainerLogReadResult> ReadAsync(
            ContainerLogQuery query,
            CancellationToken cancellationToken)
        {
            _factory.LastQuery = query;
            return Task.FromResult(new ContainerLogReadResult(
                _factory.Lines,
                false,
                _factory.Error?.Message,
                _factory.Error,
                TimeSpan.FromMilliseconds(2)));
        }

        public void Dispose()
        {
        }
    }
}

internal static class ContainerTestPayloads
{
    public const string ContainerListJson = """
        {"total":2,"items":[
          {"containerID":"aaa111","name":"web","imageName":"nginx:1.27","imageID":"sha256:1","createTime":"2026-11-10 10:00:00","state":"running","runTime":"Up 2 hours","network":["172.17.0.2"],"ports":["0.0.0.0:8080->80/tcp"],"isFromApp":true,"isFromCompose":true,"appName":"Nginx","appInstallName":"nginx","websites":["example.com"],"isPinned":true,"description":"站点"},
          {"containerID":"bbb222","name":"worker","imageName":"busybox:latest","imageID":"sha256:2","createTime":"2026-11-11 11:00:00","state":"exited","runTime":"Exited (0) 2 days ago","network":[],"ports":[],"isFromApp":false,"isFromCompose":false,"appName":"","websites":null,"isPinned":false,"description":""}
        ]}
        """;

    public const string ListStatsJson = """
        [
          {"containerID":"aaa111","cpuTotalUsage":100,"systemUsage":1000,"cpuPercent":1.5,"memoryCache":0,"memoryUsage":1048576,"memoryLimit":2147483648,"memoryPercent":2.5},
          {"containerID":"bbb222","cpuTotalUsage":0,"systemUsage":0,"cpuPercent":0,"memoryCache":0,"memoryUsage":0,"memoryLimit":0,"memoryPercent":0}
        ]
        """;

    public const string InspectJson = """
        "{\"Id\":\"aaa111\",\"Name\":\"/web\",\"Created\":\"2026-11-10T10:00:00Z\",\"State\":{\"Status\":\"running\",\"Running\":true,\"Paused\":false,\"ExitCode\":0,\"StartedAt\":\"2026-11-10T10:00:01Z\",\"FinishedAt\":\"0001-01-01T00:00:00Z\"},\"Image\":\"sha256:1\",\"RestartCount\":0,\"Config\":{\"Hostname\":\"web\",\"WorkingDir\":\"/app\",\"User\":\"root\",\"Cmd\":[\"nginx\",\"-g\",\"daemon off;\"],\"Entrypoint\":null,\"Env\":[\"MYSQL_ROOT_PASSWORD=super-secret\",\"TZ=Asia/Shanghai\"],\"Labels\":{\"com.docker.compose.project\":\"webstack\",\"com.docker.compose.service\":\"web\",\"com.docker.compose.project.working_dir\":\"/opt/webstack\"}},\"HostConfig\":{\"RestartPolicy\":{\"Name\":\"always\"},\"NetworkMode\":\"bridge\",\"Privileged\":false,\"LogConfig\":{\"Type\":\"json-file\"}},\"Mounts\":[{\"Type\":\"bind\",\"Source\":\"/data/web\",\"Destination\":\"/app\",\"Mode\":\"rw\"}],\"NetworkSettings\":{\"Networks\":{\"bridge\":{\"IPAddress\":\"172.17.0.2\"}},\"Ports\":{\"80/tcp\":[{\"HostIp\":\"0.0.0.0\",\"HostPort\":\"8080\"}]}}}"
        """;

    public const string InfoJson = """
        {"name":"web","image":"nginx:1.27","hostname":"web","domainName":"","dns":[],"networks":[{"network":"bridge","ipv4":"172.17.0.2"}],"ports":[{"hostPort":"8080","containerPort":"80","protocol":"tcp"}],"volumes":[{"type":"bind","sourceDir":"/data/web","containerDir":"/app","mode":"rw"}],"env":["MYSQL_ROOT_PASSWORD=super-secret"],"labels":[],"restartPolicy":"always","workingDir":"/app","cmd":["nginx"],"entrypoint":[],"cpuShares":0,"memory":0}
        """;

    public const string RuntimeStatsJson = """
        {"cpuPercent":1.5,"memory":12.5,"cache":0,"ioRead":1024,"ioWrite":2048,"networkRX":4096,"networkTX":8192,"shotTime":"2026-11-16T10:00:00Z"}
        """;

    public const string ItemStatsJson = """{"sizeRw":512,"sizeRootFs":4096}""";

    public const string ComposeSearchJson = """
        {"total":1,"items":[
          {"name":"webstack","createdAt":"2026-11-10 10:00:00","createdBy":"1Panel","containerCount":2,"runningCount":1,"configFile":"/opt/webstack/docker-compose.yml","workdir":"/opt/webstack","composeFileExists":true,"isPinned":false,"path":"/opt/webstack/docker-compose.yml","containers":[{"containerID":"aaa111","name":"web","createTime":"2026-11-10 10:00:00","state":"running","ports":["0.0.0.0:8080->80/tcp"]}],"env":"MYSQL_ROOT_PASSWORD=super-secret"}
        ]}
        """;
}
