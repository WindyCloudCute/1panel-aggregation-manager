using System.Text.Json;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Logs;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Logs;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Logs;

/// <summary>按节点名脚本化的假 1Panel 客户端工厂：覆盖面板/系统/网站日志 JSON 路由。</summary>
internal sealed class ScriptedLogPanelClientFactory : IPanelClientFactory
{
    private readonly Dictionary<string, LogPanelScript> _scripts = new(StringComparer.OrdinalIgnoreCase);

    public LogPanelScript Script(string nodeName)
    {
        if (!_scripts.TryGetValue(nodeName, out var script))
        {
            script = new LogPanelScript { Name = nodeName };
            _scripts[nodeName] = script;
        }

        return script;
    }

    public IPanelClient Create(NodeDescriptor node) => new ScriptedLogPanelClient(Script(node.Name));

    private sealed class ScriptedLogPanelClient : IPanelClient
    {
        private readonly LogPanelScript _script;

        public ScriptedLogPanelClient(LogPanelScript script) => _script = script;

        public NodeDescriptor Node { get; } = new(NodeId.New(), "scripted", new Uri("http://127.0.0.1:1000"), "fake-key");

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default)
        {
            if (_script.FailAll)
            {
                return Task.FromResult(Failure<T>(PanelErrorKind.Network, "假节点不可达"));
            }

            if (relativePath == LogRoutes.SystemServices)
            {
                return Task.FromResult(Success<T>(_script.Services));
            }

            if (relativePath == LogRoutes.SystemFiles)
            {
                return Task.FromResult(Success<T>(_script.Files));
            }

            return Task.FromResult(Failure<T>(PanelErrorKind.NotFound, "假客户端未实现路径：" + relativePath));
        }

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default)
        {
            _script.RequestBodies[relativePath] = JsonSerializer.Serialize(body);

            if (_script.FailAll)
            {
                return Task.FromResult(Failure<T>(PanelErrorKind.Network, "假节点不可达"));
            }

            if (relativePath == LogRoutes.PanelOperation && typeof(T) == typeof(PagedLogItemsDto<PanelOperationLogDto>))
            {
                var page = new PagedLogItemsDto<PanelOperationLogDto>
                {
                    Total = _script.OperationTotal ?? _script.OperationItems.Count,
                    Items = _script.OperationItems
                };
                return Task.FromResult(Success<T>(page));
            }

            if (relativePath == LogRoutes.PanelLogin && typeof(T) == typeof(PagedLogItemsDto<PanelLoginLogDto>))
            {
                var page = new PagedLogItemsDto<PanelLoginLogDto>
                {
                    Total = _script.LoginTotal ?? _script.LoginItems.Count,
                    Items = _script.LoginItems
                };
                return Task.FromResult(Success<T>(page));
            }

            if (relativePath == LogRoutes.SystemRead && typeof(T) == typeof(SystemLogPageDto))
            {
                var page = new SystemLogPageDto
                {
                    Source = "journalctl",
                    Items = _script.SystemItems,
                    HasMore = _script.SystemHasMore,
                    NextCursor = _script.SystemNextCursor
                };
                return Task.FromResult(Success<T>(page));
            }

            if (relativePath == LogRoutes.WebsiteSearch && typeof(T) == typeof(WebsiteLogTargetPageDto))
            {
                var page = new WebsiteLogTargetPageDto
                {
                    Total = _script.WebsiteItems.Count,
                    Items = _script.WebsiteItems
                };
                return Task.FromResult(Success<T>(page));
            }

            if (relativePath == LogRoutes.WebsiteLog && typeof(T) == typeof(WebsiteLogDto))
            {
                var dto = new WebsiteLogDto
                {
                    Enable = _script.WebsiteLogEnabled,
                    Content = _script.WebsiteLogContent,
                    End = _script.WebsiteLogEnd,
                    Path = "/logs/access.log"
                };
                return Task.FromResult(Success<T>(dto));
            }

            return Task.FromResult(Failure<T>(PanelErrorKind.NotFound, "假客户端未实现路径：" + relativePath));
        }

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

        private static PanelCallResult<T> Success<T>(object data) =>
            new((T)data, null, TimeSpan.Zero);

        private static PanelCallResult<T> Failure<T>(PanelErrorKind kind, string message) =>
            new(default, new PanelError(kind, message), TimeSpan.Zero);
    }
}

/// <summary>单个节点的假日志数据脚本。</summary>
internal sealed class LogPanelScript
{
    public string Name { get; set; } = "node";

    public bool FailAll { get; set; }

    public List<PanelOperationLogDto> OperationItems { get; } = [];

    public long? OperationTotal { get; set; }

    public List<PanelLoginLogDto> LoginItems { get; } = [];

    public long? LoginTotal { get; set; }

    public List<SystemLogItemDto> SystemItems { get; } = [];

    public bool SystemHasMore { get; set; }

    public string? SystemNextCursor { get; set; }

    public List<WebsiteLogTargetDto> WebsiteItems { get; } = [];

    public string WebsiteLogContent { get; set; } = "line-1\nline-2";

    public bool WebsiteLogEnabled { get; set; } = true;

    public bool WebsiteLogEnd { get; set; } = true;

    public string[] Services { get; set; } = ["1panel-agent.service", "docker.service"];

    public string[] Files { get; set; } = ["2026-11-16", "2026-11-15"];

    public Dictionary<string, string> RequestBodies { get; } = new(StringComparer.Ordinal);
}

/// <summary>假容器日志域客户端工厂（复用 P6 的接口，便于 P10 单测容器范围）。</summary>
internal sealed class FakeLogContainerClientFactory : IContainerLogClientFactory
{
    private readonly Dictionary<string, LogContainerScript> _scripts = new(StringComparer.OrdinalIgnoreCase);

    public LogContainerScript Script(string nodeName)
    {
        if (!_scripts.TryGetValue(nodeName, out var script))
        {
            script = new LogContainerScript();
            _scripts[nodeName] = script;
        }

        return script;
    }

    public IContainerLogClient Create(NodeDescriptor node) =>
        new FakeContainerLogClient(Script(node.Name));

    private sealed class FakeContainerLogClient : IContainerLogClient
    {
        private readonly LogContainerScript _script;

        public FakeContainerLogClient(LogContainerScript script) => _script = script;

        public Task<ContainerLogReadResult> ReadAsync(
            ContainerLogQuery query,
            CancellationToken cancellationToken)
        {
            _script.LastQuery = query;
            _script.Calls++;
            return Task.FromResult(new ContainerLogReadResult(
                _script.Lines,
                _script.Truncated,
                _script.Error?.Message,
                _script.Error,
                TimeSpan.Zero));
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class LogContainerScript
{
    public List<string> Lines { get; set; } = ["docker-line-1", "docker-line-2"];

    public bool Truncated { get; set; }

    public PanelError? Error { get; set; }

    public int Calls { get; set; }

    public ContainerLogQuery? LastQuery { get; set; }
}

/// <summary>P10 单测共享数据。</summary>
internal static class LogTestData
{
    public static NodeRecord Node(string name, ScriptedLogPanelClientFactory factory, bool enabled = true)
    {
        factory.Script(name);
        return new NodeRecord(
            NodeId.New(),
            name,
            new Uri("http://127.0.0.1:1000"),
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
    }

    public static PanelOperationLogDto Operation(
        string detail,
        string? source = "settings",
        string status = "Success",
        DateTimeOffset? createdAt = null) => new()
    {
        Id = Random.Shared.NextInt64(1, 1000),
        Source = source,
        User = "admin",
        Ip = "198.51.100.1",
        Path = "/core/settings/upgrade",
        Method = "post",
        Status = status,
        Message = string.Empty,
        DetailZH = detail,
        Latency = 39_353_655,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow
    };

    public static PanelLoginLogDto Login(
        string status = "Success",
        DateTimeOffset? createdAt = null) => new()
    {
        Id = Random.Shared.NextInt64(1, 1000),
        Ip = "198.51.100.1",
        User = "admin",
        Address = "Intranet",
        Agent = "Mozilla/5.0",
        Status = status,
        Message = string.Empty,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow
    };

    public static SystemLogItemDto System(
        string message,
        string service = "1panel-agent.service",
        string priority = "6",
        string time = "2026-11-16 23:59:35") => new()
    {
        Time = time,
        Priority = priority,
        Service = service,
        Message = message
    };

    public static WebsiteLogTargetDto Website(long id, string domain) => new()
    {
        Id = id,
        PrimaryDomain = domain,
        Alias = domain.Split('.')[0]
    };
}

/// <summary>P10 单测 Harness：真实 <see cref="LogService"/> + 假节点/客户端。</summary>
internal sealed class LogHarness
{
    public LogHarness()
    {
        Service = new LogService(
            Nodes,
            new PanelFleet.Client.UnitTests.Operations.FakeSecretProtector(),
            ClientFactory,
            ContainerFactory,
            Options,
            new PanelFleet.Client.UnitTests.Overview.TestTimeProvider(
                new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LogService>.Instance);
    }

    public ScriptedLogPanelClientFactory ClientFactory { get; } = new();

    public FakeLogContainerClientFactory ContainerFactory { get; } = new();

    public PanelFleet.Client.UnitTests.Operations.FakeNodeStore Nodes { get; } = new();

    public LogOptions Options { get; } = new();

    public LogService Service { get; }

    public NodeRecord AddNode(string name, bool enabled = true)
    {
        var node = LogTestData.Node(name, ClientFactory, enabled);
        Nodes.Nodes.Add(node);
        return node;
    }

    public LogPanelScript Script(string name) => ClientFactory.Script(name);
}
