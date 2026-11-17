using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Logs;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Logs;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P10 系统日志集成测试：PanelFleetWebApplicationFactory + AdditionalServices 注册 AddLogModule +
/// 自建假 IPanelClientFactory / IContainerLogClientFactory（不改共享假面板类，不访问真实节点）。
/// 覆盖页面渲染、401、查询参数契约、过滤与行数上限、节点错误隔离。
/// </summary>
public sealed class LogApiTests
{
    [Fact]
    public async Task LogsPage_RendersForAdmin()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("系统日志", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogsPage_AcceptsContainerQueryContract()
    {
        var panels = new ScriptedLogPanelClientFactory();
        var containers = new FakeLogContainerClientFactory();
        using var factory = CreateFactory(panels, containers);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");
        containers.Script("node-a").Lines = ["docker-line-1", "docker-line-2"];

        var response = await client.GetAsync($"/logs?type=container&nodeId={nodeId:D}&container=1Panel-jenkins");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("容器日志", html, StringComparison.Ordinal);
        Assert.Contains("docker-line-1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_Unauthenticated_Returns401()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/logs?type=panel");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_PanelScope_ReturnsEntriesAndAppliesLimit()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");
        var script = panels.Script("node-a");
        for (var index = 0; index < 5; index++)
        {
            script.OperationItems.Add(Operation("操作 " + index));
        }

        var response = await client.GetAsync($"/api/logs?type=panel&nodeId={nodeId:D}&limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("nodesSucceeded").GetInt32());
        var node = document.RootElement.GetProperty("nodes")[0];
        Assert.Equal(2, node.GetProperty("entries").GetArrayLength());
        Assert.True(node.GetProperty("truncated").GetBoolean());
        Assert.Contains(
            "操作 0",
            node.GetProperty("entries")[0].GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_SystemScope_AppliesKeywordFilter_AndIsolatesNodeFailure()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");
        await CreateNodeAsync(client, token, cookie, "node-b");
        panels.Script("node-a").SystemItems.Add(System("docker started", "docker.service"));
        panels.Script("node-a").SystemItems.Add(System("nginx reloaded", "nginx.service"));
        panels.Script("node-b").FailAll = true;

        var response = await client.GetAsync("/api/logs?type=system&keyword=docker&limit=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("nodesTotal").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("nodesFailed").GetInt32());

        var nodeA = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("nodeName").GetString() == "node-a");
        Assert.Equal(1, nodeA.GetProperty("entries").GetArrayLength());
        Assert.Contains(
            "docker",
            nodeA.GetProperty("entries")[0].GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_ContainerScope_ReturnsDockerLines()
    {
        var panels = new ScriptedLogPanelClientFactory();
        var containers = new FakeLogContainerClientFactory();
        using var factory = CreateFactory(panels, containers);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");
        containers.Script("node-a").Lines = ["docker-line-1", "docker-line-2"];

        var response = await client.GetAsync(
            $"/api/logs?type=container&nodeId={nodeId:D}&container=1Panel-jenkins&limit=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var node = document.RootElement.GetProperty("nodes")[0];
        Assert.Equal(2, node.GetProperty("entries").GetArrayLength());
        Assert.Equal("1Panel-jenkins", node.GetProperty("entries")[0].GetProperty("service").GetString());
    }

    [Fact]
    public async Task Api_WebsiteScope_ResolvesDomainThenReadsLog()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");
        var script = panels.Script("node-a");
        script.WebsiteItems.Add(new WebsiteLogTargetDto { Id = 7, PrimaryDomain = "example.com", Alias = "example" });
        script.WebsiteLogContent = "127.0.0.1 - GET /a\n127.0.0.1 - GET /b";

        var response = await client.GetAsync($"/api/logs?type=website&nodeId={nodeId:D}&website=example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var node = document.RootElement.GetProperty("nodes")[0];
        Assert.Equal(2, node.GetProperty("entries").GetArrayLength());
        Assert.Equal("example.com", node.GetProperty("entries")[0].GetProperty("service").GetString());
        Assert.Contains("\"id\":7", script.RequestBodies[LogRoutes.WebsiteLog], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_InvalidScope_Returns400()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/api/logs?type=unknown");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_ContainerWithoutContainer_Returns400()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");

        var response = await client.GetAsync($"/api/logs?type=container&nodeId={nodeId:D}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_UnknownNode_Returns404()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/logs?type=panel&nodeId={Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Api_Filters_ReturnsSystemServices()
    {
        var panels = new ScriptedLogPanelClientFactory();
        using var factory = CreateFactory(panels, new FakeLogContainerClientFactory());
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");
        panels.Script("node-a").Services = ["docker.service", "ssh.service"];

        var response = await client.GetAsync("/api/logs/filters");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var services = document.RootElement.GetProperty("services").EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("docker.service", services);
        Assert.Contains("ssh.service", services);
    }

    private static PanelFleetWebApplicationFactory CreateFactory(
        ScriptedLogPanelClientFactory panels,
        FakeLogContainerClientFactory containers)
    {
        return new PanelFleetWebApplicationFactory
        {
            AdditionalServices = services =>
            {
                services.RemoveAll<IPanelClientFactory>();
                services.AddSingleton<IPanelClientFactory>(panels);
                services.RemoveAll<IContainerLogClientFactory>();
                services.AddSingleton<IContainerLogClientFactory>(containers);
                services.AddLogModule(options =>
                {
                    options.NodeTimeoutSeconds = 5;
                    options.BudgetSeconds = 10;
                });
            }
        };
    }

    private static PanelOperationLogDto Operation(string detail) => new()
    {
        Id = Random.Shared.NextInt64(1, 10_000),
        Source = "settings",
        User = "admin",
        Ip = "198.51.100.1",
        Path = "/core/settings/upgrade",
        Method = "post",
        Status = "Success",
        Message = string.Empty,
        DetailZH = detail,
        Latency = 1_000_000,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static SystemLogItemDto System(string message, string service) => new()
    {
        Time = "2026-11-17 07:00:00",
        Priority = "6",
        Service = service,
        Message = message
    };

    private static async Task<Guid> CreateNodeAsync(
        HttpClient client,
        string token,
        string cookie,
        string name)
    {
        var request = CreateJsonRequest(
            HttpMethod.Post,
            "/api/nodes",
            new
            {
                name,
                baseUrl = $"https://{name}.example.test",
                apiKey = "test-api-key-1234",
                authMode = "Auto",
                tlsPolicy = "Strict",
                isEnabled = true,
                tags = Array.Empty<string>()
            },
            token,
            cookie);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        object? body,
        string token,
        string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static async Task<(string Token, string Cookie)> GetCsrfAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/antiforgery/token");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("缺少防伪令牌。");

        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith("PanelFleet.Antiforgery=", StringComparison.Ordinal))
            : null;

        var cookie = setCookie?.Split(';')[0]
            ?? throw new InvalidOperationException("缺少防伪 Cookie。");

        return (token, cookie);
    }

    /// <summary>按节点名脚本化的假 1Panel 客户端工厂（只用于集成测试）。</summary>
    private sealed class ScriptedLogPanelClientFactory : IPanelClientFactory
    {
        private readonly ConcurrentDictionary<string, LogPanelScript> _scripts =
            new(StringComparer.OrdinalIgnoreCase);

        public LogPanelScript Script(string nodeName) =>
            _scripts.GetOrAdd(nodeName, name => new LogPanelScript { Name = name });

        public IPanelClient Create(NodeDescriptor node) => new FakePanelClient(Script(node.Name));

        private sealed class FakePanelClient : IPanelClient
        {
            private readonly LogPanelScript _script;

            public FakePanelClient(LogPanelScript script) => _script = script;

            public NodeDescriptor Node { get; } = new(
                NodeId.New(),
                "scripted",
                new Uri("http://127.0.0.1:1000"),
                "fake-api-key");

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

                if (relativePath == LogRoutes.PanelOperation
                    && typeof(T) == typeof(PagedLogItemsDto<PanelOperationLogDto>))
                {
                    return Task.FromResult(Success<T>(new PagedLogItemsDto<PanelOperationLogDto>
                    {
                        Total = _script.OperationItems.Count,
                        Items = _script.OperationItems
                    }));
                }

                if (relativePath == LogRoutes.PanelLogin
                    && typeof(T) == typeof(PagedLogItemsDto<PanelLoginLogDto>))
                {
                    return Task.FromResult(Success<T>(new PagedLogItemsDto<PanelLoginLogDto>
                    {
                        Total = 0,
                        Items = []
                    }));
                }

                if (relativePath == LogRoutes.SystemRead && typeof(T) == typeof(SystemLogPageDto))
                {
                    return Task.FromResult(Success<T>(new SystemLogPageDto
                    {
                        Source = "journalctl",
                        Items = _script.SystemItems,
                        HasMore = _script.SystemHasMore,
                        NextCursor = _script.SystemNextCursor
                    }));
                }

                if (relativePath == LogRoutes.WebsiteSearch && typeof(T) == typeof(WebsiteLogTargetPageDto))
                {
                    return Task.FromResult(Success<T>(new WebsiteLogTargetPageDto
                    {
                        Total = _script.WebsiteItems.Count,
                        Items = _script.WebsiteItems
                    }));
                }

                if (relativePath == LogRoutes.WebsiteLog && typeof(T) == typeof(WebsiteLogDto))
                {
                    return Task.FromResult(Success<T>(new WebsiteLogDto
                    {
                        Enable = true,
                        Content = _script.WebsiteLogContent,
                        End = true,
                        Path = "/logs/access.log"
                    }));
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

            private static PanelCallResult<T> Success<T>(object data) => new((T)data, null, TimeSpan.Zero);

            private static PanelCallResult<T> Failure<T>(PanelErrorKind kind, string message) =>
                new(default, new PanelError(kind, message), TimeSpan.Zero);
        }
    }

    private sealed class LogPanelScript
    {
        public string Name { get; set; } = "node";

        public bool FailAll { get; set; }

        public List<PanelOperationLogDto> OperationItems { get; } = [];

        public List<SystemLogItemDto> SystemItems { get; } = [];

        public bool SystemHasMore { get; set; }

        public string? SystemNextCursor { get; set; }

        public List<WebsiteLogTargetDto> WebsiteItems { get; } = [];

        public string WebsiteLogContent { get; set; } = "line-1\nline-2";

        public string[] Services { get; set; } = ["1panel-agent.service", "docker.service"];

        public string[] Files { get; set; } = ["2026-11-17", "2026-11-16"];

        public Dictionary<string, string> RequestBodies { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>假容器日志域客户端工厂。</summary>
    private sealed class FakeLogContainerClientFactory : IContainerLogClientFactory
    {
        private readonly ConcurrentDictionary<string, FakeContainerLogClient> _clients =
            new(StringComparer.OrdinalIgnoreCase);

        public FakeContainerLogClient Script(string nodeName) =>
            _clients.GetOrAdd(nodeName, _ => new FakeContainerLogClient());

        public IContainerLogClient Create(NodeDescriptor node) => Script(node.Name);

        internal sealed class FakeContainerLogClient : IContainerLogClient
        {
            public List<string> Lines { get; set; } = ["docker-line-1", "docker-line-2"];

            public Task<ContainerLogReadResult> ReadAsync(
                ContainerLogQuery query,
                CancellationToken cancellationToken) =>
                Task.FromResult(new ContainerLogReadResult(Lines, false, null, null, TimeSpan.Zero));

            public void Dispose()
            {
            }
        }
    }
}
