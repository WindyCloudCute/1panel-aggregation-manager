using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Containers;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Containers;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;
using ContainerItemDto = PanelFleet.Client.Dto.Containers.ContainerItemDto;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P6 容器管理集成测试：PanelFleetWebApplicationFactory + ConfigureTestServices 注册容器模块与
/// 自建假 IPanelClientFactory，经内核验证 预览 → 确认 → 执行 → 审计、批量失败停止与批量上限。
/// </summary>
public sealed class ContainerApiTests
{
    [Fact]
    public async Task PreviewExecuteStop_ExecutesAndWritesAudit()
    {
        using var host = CreateHost(maxBatchNodes: 5);
        var node = await CreateNodeAsync(host, "node-a");
        using var scope = host.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();

        var invocation = new OperationInvocation(
            "container.stop",
            [node.Id],
            new System.Text.Json.Nodes.JsonObject { ["container"] = "web" });
        var preview = await operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationRisk.Medium, preview.Risk);
        Assert.False(preview.RequiresTypedConfirmation);

        var execution = await operations.ExecuteAsync(preview.TokenId, null, ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, execution.Status);
        var step = Assert.Single(execution.Steps);
        Assert.True(step.Success);
        Assert.False(string.IsNullOrWhiteSpace(step.TaskId));
        Assert.Equal(1, host.Panels.OperateCalls("node-a"));
        Assert.Equal("exited", host.Panels.StateOf("node-a", "web"));

        var events = await audit.ListAsync(20, "operation.executed", null, CancellationToken.None);
        Assert.Contains(events, entry =>
            entry.NodeId == node.Id
            && entry.DetailJson is not null
            && entry.DetailJson.Contains("container.stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_RequiresTypedConfirmation_AndRejectsMismatch()
    {
        using var host = CreateHost();
        var node = await CreateNodeAsync(host, "node-a");
        using var scope = host.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();

        var invocation = new OperationInvocation(
            "container.delete",
            [node.Id],
            new System.Text.Json.Nodes.JsonObject { ["container"] = "web" });
        var preview = await operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationRisk.High, preview.Risk);
        Assert.True(preview.RequiresTypedConfirmation);
        Assert.Equal("node-a", preview.ConfirmText);

        await Assert.ThrowsAsync<OperationConfirmationException>(
            () => operations.ExecuteAsync(preview.TokenId, "wrong-text", ActorInfo.System, CancellationToken.None));

        var rejections = await audit.ListAsync(20, "operation.rejected", null, CancellationToken.None);
        Assert.Contains(rejections, entry =>
            entry.DetailJson is not null
            && entry.DetailJson.Contains("typed_confirmation_mismatch", StringComparison.Ordinal));

        var retry = await operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None);
        var execution = await operations.ExecuteAsync(retry.TokenId, "node-a", ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, execution.Status);
        Assert.True(host.Panels.IsRemoved("node-a", "web"));
    }

    [Fact]
    public async Task Batch_StopsOnFirstNodeFailure()
    {
        using var host = CreateHost();
        var nodeA = await CreateNodeAsync(host, "node-a");
        var nodeB = await CreateNodeAsync(host, "node-b");
        var nodeC = await CreateNodeAsync(host, "node-c");
        host.Panels.SetOperateFailure("node-b");

        using var scope = host.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();

        var invocation = new OperationInvocation(
            "container.restart",
            [nodeA.Id, nodeB.Id, nodeC.Id],
            new System.Text.Json.Nodes.JsonObject { ["container"] = "web" });
        var preview = await operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, preview.Risk);
        Assert.True(preview.RequiresTypedConfirmation);
        Assert.False(string.IsNullOrWhiteSpace(preview.ConfirmText));

        var execution = await operations.ExecuteAsync(
            preview.TokenId,
            preview.ConfirmText,
            ActorInfo.System,
            CancellationToken.None);

        Assert.Equal(OperationStatus.PartiallySucceeded, execution.Status);
        Assert.Equal(2, execution.Steps.Count);
        Assert.True(execution.Steps[0].Success);
        Assert.False(execution.Steps[1].Success);
        Assert.Equal(1, host.Panels.OperateCalls("node-a"));
        Assert.Equal(1, host.Panels.OperateCalls("node-b"));
        Assert.Equal(0, host.Panels.OperateCalls("node-c"));

        var batchEvents = await audit.ListAsync(20, "operation.batch", null, CancellationToken.None);
        Assert.Contains(batchEvents, entry =>
            entry.DetailJson is not null
            && entry.DetailJson.Contains("PartiallySucceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_BatchExceedingNodeLimit_IsRejected()
    {
        using var host = CreateHost(maxBatchNodes: 2);
        var nodeA = await CreateNodeAsync(host, "node-a");
        var nodeB = await CreateNodeAsync(host, "node-b");
        var nodeC = await CreateNodeAsync(host, "node-c");
        using var scope = host.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();

        var invocation = new OperationInvocation(
            "container.start",
            [nodeA.Id, nodeB.Id, nodeC.Id],
            new System.Text.Json.Nodes.JsonObject { ["container"] = "web" });

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None));

        Assert.Contains("最多 2 个节点", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Panels.TotalOperateCalls);
    }

    [Fact]
    public async Task ListAndLogs_ServiceLevel_Work()
    {
        using var host = CreateHost();
        await CreateNodeAsync(host, "node-a");
        await CreateNodeAsync(host, "node-b");
        using var scope = host.Services.CreateAsyncScope();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerService>();

        var list = await containers.ListAsync(
            new ContainerQuery(null, null, "all", "name", "ascending", 1, 50),
            CancellationToken.None);

        Assert.Equal(2, list.NodesSucceeded);
        Assert.Equal(0, list.NodesFailed);
        Assert.Equal(4, list.Rows.Count);
        Assert.Contains(list.Rows, row => row.Name == "web" && row.Stats is not null);

        var log = await containers.GetLogsAsync(
            list.Nodes[0].NodeId,
            new ContainerLogRequest("web", "all", 100),
            CancellationToken.None);

        Assert.Null(log.Error);
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public async Task ComposeContentEndpoint_ReturnsPathAndYaml()
    {
        using var host = CreateHost();
        var node = await CreateNodeAsync(host, "node-a");

        var response = await host.Client.GetAsync(
            "/api/containers/compose/content?nodeId=" + node.Id.Value + "&name=webstack",
            CancellationToken.None);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("/opt/webstack/docker-compose.yml", document.RootElement.GetProperty("path").GetString());
        Assert.Contains("services:", document.RootElement.GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ComposeCreate_PreviewAndExecute_SubmitsTaskId()
    {
        using var host = CreateHost();
        var node = await CreateNodeAsync(host, "node-a");
        using var scope = host.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();

        var invocation = new OperationInvocation(
            "compose.create",
            [node.Id],
            new System.Text.Json.Nodes.JsonObject
            {
                ["compose"] = "webstack",
                ["content"] = "services: {}"
            });
        var preview = await operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationRisk.High, preview.Risk);
        Assert.True(preview.RequiresTypedConfirmation);
        Assert.False(string.IsNullOrWhiteSpace(preview.ConfirmText));

        var execution = await operations.ExecuteAsync(preview.TokenId, preview.ConfirmText, ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, execution.Status);
        var step = Assert.Single(execution.Steps);
        Assert.True(step.Success);
        Assert.False(string.IsNullOrWhiteSpace(step.TaskId));
        Assert.Equal(1, host.Panels.ComposeCreateCalls("node-a"));
    }

    [Fact]
    public async Task ComposeDelete_RequiresProjectName_AndExecutesDelete()
    {
        using var host = CreateHost();
        var node = await CreateNodeAsync(host, "node-a");
        using var scope = host.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();

        var invocation = new OperationInvocation(
            "compose.delete",
            [node.Id],
            new System.Text.Json.Nodes.JsonObject
            {
                ["compose"] = "webstack",
                ["path"] = "/opt/webstack",
                ["withFile"] = true
            });
        var preview = await operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, preview.Risk);
        Assert.Equal("webstack", preview.ConfirmText);

        await Assert.ThrowsAsync<OperationConfirmationException>(
            () => operations.ExecuteAsync(preview.TokenId, "wrong", ActorInfo.System, CancellationToken.None));

        var retry = await operations.PreviewAsync(invocation, ActorInfo.System, CancellationToken.None);
        var execution = await operations.ExecuteAsync(retry.TokenId, "webstack", ActorInfo.System, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, execution.Status);
        Assert.True(Assert.Single(execution.Steps).Success);
        Assert.Equal(1, host.Panels.ComposeOperateCalls("node-a"));

        var events = await audit.ListAsync(20, "operation.executed", null, CancellationToken.None);
        Assert.Contains(events, entry =>
            entry.NodeId == node.Id
            && entry.DetailJson is not null
            && entry.DetailJson.Contains("compose.delete", StringComparison.Ordinal));
    }

    private static ContainerHost CreateHost(int maxBatchNodes = 50)
    {
        var baseFactory = new PanelFleetWebApplicationFactory();
        var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // 移除生产接线的容器服务/处理器，重新按测试需要注册（AddContainerModule 幂等）。
            services.RemoveAll<IOperationHandler>();
            services.RemoveAll<IContainerService>();
            services.RemoveAll<IContainerLogClientFactory>();
            services.RemoveAll<ContainerOptions>();
            services.AddContainerModule();

            services.RemoveAll<ContainerOptions>();
            services.AddSingleton(new ContainerOptions { VerifyTimeoutSeconds = 2, MaxConcurrency = 4, PerNodePageSize = 50 });

            services.RemoveAll<OperationOptions>();
            services.AddSingleton(new OperationOptions { MaxBatchNodes = maxBatchNodes });

            var panels = new ContainerFakePanelFactory();
            services.AddSingleton(panels);
            services.RemoveAll<IPanelClientFactory>();
            services.AddSingleton<IPanelClientFactory>(serviceProvider =>
                serviceProvider.GetRequiredService<ContainerFakePanelFactory>());

            services.RemoveAll<IContainerLogClientFactory>();
            services.AddSingleton<IContainerLogClientFactory, ContainerFakeLogClientFactory>();
        }));

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "admin");
        return new ContainerHost(baseFactory, factory, client);
    }

    private static async Task<NodeSummary> CreateNodeAsync(ContainerHost host, string name)
    {
        using var scope = host.Services.CreateAsyncScope();
        var nodes = scope.ServiceProvider.GetRequiredService<INodeService>();
        return await nodes.CreateAsync(
            new CreateNodeRequest(
                name,
                "https://" + name + ".example.test",
                "test-api-key-1234",
                AuthMode.Auto,
                TlsPolicy.Strict,
                IsEnabled: true),
            ActorInfo.System,
            CancellationToken.None);
    }

    private sealed class ContainerHost : IDisposable
    {
        private readonly PanelFleetWebApplicationFactory _baseFactory;

        public ContainerHost(
            PanelFleetWebApplicationFactory baseFactory,
            WebApplicationFactory<Program> factory,
            HttpClient client)
        {
            _baseFactory = baseFactory;
            Factory = factory;
            Client = client;
        }

        public WebApplicationFactory<Program> Factory { get; }

        public HttpClient Client { get; }

        public IServiceProvider Services => Factory.Services;

        public ContainerFakePanelFactory Panels => Services.GetRequiredService<ContainerFakePanelFactory>();

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            _baseFactory.Dispose();
        }
    }
}

/// <summary>集成测试假 1Panel 客户端工厂：脚本化容器状态、操作失败与调用计数。</summary>
public sealed class ContainerFakePanelFactory : IPanelClientFactory
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _operateCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _composeOperateCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _composeCreateCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _composeUpdateCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _operateFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ContainerItemDto>> _containers = new(StringComparer.OrdinalIgnoreCase);

    public int TotalOperateCalls => _operateCalls.Values.Sum();

    public int OperateCalls(string nodeName) => _operateCalls.TryGetValue(nodeName, out var count) ? count : 0;

    public int ComposeOperateCalls(string nodeName) => _composeOperateCalls.TryGetValue(nodeName, out var count) ? count : 0;

    public int ComposeCreateCalls(string nodeName) => _composeCreateCalls.TryGetValue(nodeName, out var count) ? count : 0;

    public int ComposeUpdateCalls(string nodeName) => _composeUpdateCalls.TryGetValue(nodeName, out var count) ? count : 0;

    public void SetOperateFailure(string nodeName) => _operateFailures[nodeName] = true;

    public string? StateOf(string nodeName, string containerName) =>
        StatesFor(nodeName).TryGetValue(containerName, out var state) ? state : null;

    public bool IsRemoved(string nodeName, string containerName) =>
        !StatesFor(nodeName).ContainsKey(containerName);

    public IPanelClient Create(NodeDescriptor node) => new FakeClient(this, node);

    private ConcurrentDictionary<string, string> StatesFor(string nodeName) =>
        _states.GetOrAdd(
            nodeName,
            _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["web"] = "running",
                ["worker"] = "exited"
            });

    private List<ContainerItemDto> ContainersFor(string nodeName) =>
        _containers.GetOrAdd(nodeName, _ => CreateInitialContainers());

    private static List<ContainerItemDto> CreateInitialContainers() =>
    [
        new ContainerItemDto
        {
            ContainerID = "aaa111",
            Name = "web",
            ImageID = "sha256:1",
            ImageName = "nginx:1.27",
            CreateTime = "2026-11-10 10:00:00",
            State = "running",
            RunTime = "Up 2 hours",
            Network = ["172.17.0.2"],
            Ports = ["0.0.0.0:8080->80/tcp"],
            IsFromApp = true,
            IsFromCompose = true,
            AppName = "Nginx",
            AppInstallName = "nginx",
            Websites = ["example.com"]
        },
        new ContainerItemDto
        {
            ContainerID = "bbb222",
            Name = "worker",
            ImageID = "sha256:2",
            ImageName = "busybox:latest",
            CreateTime = "2026-11-11 11:00:00",
            State = "exited",
            RunTime = "Exited (0) 2 days ago"
        }
    ];

    private sealed class FakeClient : IPanelClient
    {
        private readonly ContainerFakePanelFactory _factory;

        public FakeClient(ContainerFakePanelFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Respond<T>(relativePath, body: null, isPost: false));

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Respond<T>(relativePath, body, isPost: true));

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

        private PanelCallResult<T> Respond<T>(string relativePath, object? body, bool isPost)
        {
            var elapsed = TimeSpan.FromMilliseconds(2);

            if (relativePath.Contains("containers/operate", StringComparison.OrdinalIgnoreCase))
            {
                return Operate<T>(body, elapsed);
            }

            if (relativePath.Contains("containers/compose/operate", StringComparison.OrdinalIgnoreCase))
            {
                _factory._composeOperateCalls.AddOrUpdate(Node.Name, 1, static (_, count) => count + 1);
                return new PanelCallResult<T>(JsonSerializer.Deserialize<T>("{}", JsonOptions), null, elapsed);
            }

            if (relativePath.Contains("containers/compose/update", StringComparison.OrdinalIgnoreCase))
            {
                _factory._composeUpdateCalls.AddOrUpdate(Node.Name, 1, static (_, count) => count + 1);
                return Json<T>(new { }, elapsed);
            }

            if (relativePath.Contains("files/content", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = ReadPath(body) ?? "/opt/webstack/docker-compose.yml";
                return Json<T>(
                    new
                    {
                        content = "services:\n  web:\n    image: nginx:1.27\n",
                        size = 39,
                        path = filePath,
                        name = "docker-compose.yml",
                        mimeType = "text/yaml",
                        extension = ".yml",
                        isDir = false
                    },
                    elapsed);
            }

            if (relativePath.EndsWith("containers/compose", StringComparison.OrdinalIgnoreCase))
            {
                _factory._composeCreateCalls.AddOrUpdate(Node.Name, 1, static (_, count) => count + 1);
                return Json<T>(new { }, elapsed);
            }

            if (relativePath.Contains("containers/search/log", StringComparison.OrdinalIgnoreCase))
            {
                return new PanelCallResult<T>(default, null, elapsed);
            }

            if (relativePath.Contains("containers/compose/search", StringComparison.OrdinalIgnoreCase))
            {
                var project = new ComposeItemDto
                {
                    Name = "webstack",
                    CreatedAt = "2026-11-10 10:00:00",
                    CreatedBy = "1Panel",
                    ContainerCount = 2,
                    RunningCount = 1,
                    ConfigFile = "/opt/webstack/docker-compose.yml",
                    Workdir = "/opt/webstack",
                    ComposeFileExists = true,
                    Path = "/opt/webstack/docker-compose.yml",
                    Containers =
                    [
                        new ComposeContainerDto { ContainerID = "aaa111", Name = "web", State = "running" },
                        new ComposeContainerDto { ContainerID = "bbb222", Name = "worker", State = "exited" }
                    ]
                };
                return Json<T>(new PagedItemsDto<ComposeItemDto> { Total = 1, Items = [project] }, elapsed);
            }

            if (relativePath.Contains("containers/list/stats", StringComparison.OrdinalIgnoreCase))
            {
                var stats = _factory.ContainersFor(Node.Name)
                    .Select(item => new ContainerListStatsDto
                    {
                        ContainerID = item.ContainerID,
                        CpuPercent = item.State == "running" ? 1.5 : 0,
                        MemoryUsage = item.State == "running" ? 1048576 : 0,
                        MemoryLimit = 2147483648,
                        MemoryPercent = item.State == "running" ? 2.5 : 0
                    })
                    .ToList();
                return Json<T>(stats, elapsed);
            }

            if (relativePath.Contains("containers/item/stats", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new ContainerItemStatsDto { SizeRw = 512, SizeRootFs = 4096 }, elapsed);
            }

            if (relativePath.Contains("containers/stats/", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(
                    new ContainerRuntimeStatsDto
                    {
                        CpuPercent = 1.5,
                        Memory = 12.5,
                        IoRead = 1024,
                        IoWrite = 2048,
                        NetworkRX = 4096,
                        NetworkTX = 8192,
                        ShotTime = DateTimeOffset.UtcNow
                    },
                    elapsed);
            }

            if (relativePath.Contains("containers/inspect", StringComparison.OrdinalIgnoreCase))
            {
                var name = ReadName(body) ?? "web";
                if (!_factory.StatesFor(Node.Name).TryGetValue(name, out var current))
                {
                    return new PanelCallResult<T>(
                        default,
                        new PanelError(PanelErrorKind.NotFound, "[" + Node.Name + "] 容器不存在：" + name),
                        elapsed);
                }

                var inspect = JsonSerializer.Serialize(new { State = new { Status = current } });
                return Json<T>(inspect, elapsed);
            }

            if (relativePath.Contains("containers/info", StringComparison.OrdinalIgnoreCase))
            {
                var name = ReadName(body) ?? "web";
                return Json<T>(
                    new
                    {
                        name,
                        image = "nginx:1.27",
                        hostname = name,
                        networks = new[] { new { network = "bridge", ipv4 = "172.17.0.2" } },
                        ports = new[] { new { hostPort = "8080", containerPort = "80", protocol = "tcp" } },
                        env = new[] { "MYSQL_ROOT_PASSWORD=super-secret" },
                        restartPolicy = "always",
                        workingDir = "/app"
                    },
                    elapsed);
            }

            if (relativePath.Contains("containers/search", StringComparison.OrdinalIgnoreCase))
            {
                var states = _factory.StatesFor(Node.Name);
                var items = _factory.ContainersFor(Node.Name)
                    .Where(item => !string.IsNullOrWhiteSpace(item.Name) && states.ContainsKey(item.Name))
                    .Select(item => CloneWithState(item, states[item.Name!]))
                    .ToList();
                return Json<T>(new PagedItemsDto<ContainerItemDto> { Total = items.Count, Items = items }, elapsed);
            }

            return new PanelCallResult<T>(
                default,
                new PanelError(PanelErrorKind.NotFound, "[" + Node.Name + "] 假客户端未实现路径：" + relativePath),
                elapsed);
        }

        private PanelCallResult<T> Operate<T>(object? body, TimeSpan elapsed)
        {
            _factory._operateCalls.AddOrUpdate(Node.Name, 1, static (_, count) => count + 1);

            if (_factory._operateFailures.ContainsKey(Node.Name))
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(PanelErrorKind.PanelServerError, "[" + Node.Name + "] 模拟操作失败"),
                    elapsed);
            }

            if (body is not ContainerOperateRequest request)
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(PanelErrorKind.Validation, "[" + Node.Name + "] 缺少操作参数"),
                    elapsed);
            }

            var states = _factory.StatesFor(Node.Name);
            foreach (var name in request.Names)
            {
                switch (request.Operation)
                {
                    case "start":
                    case "restart":
                    case "unpause":
                        states[name] = "running";
                        break;
                    case "stop":
                        states[name] = "exited";
                        break;
                    case "pause":
                        states[name] = "paused";
                        break;
                    case "remove":
                        states.TryRemove(name, out _);
                        break;
                }
            }

            return Json<T>(new { }, elapsed);
        }

        private static string? ReadPath(object? body)
        {
            if (body is null)
            {
                return null;
            }

            var json = JsonSerializer.Serialize(body, JsonOptions);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null;
        }

        private static string? ReadName(object? body)
        {
            if (body is null)
            {
                return null;
            }

            var json = JsonSerializer.Serialize(body, JsonOptions);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }

        private static ContainerItemDto CloneWithState(ContainerItemDto item, string state) => new()
        {
            ContainerID = item.ContainerID,
            Name = item.Name,
            ImageID = item.ImageID,
            ImageName = item.ImageName,
            CreateTime = item.CreateTime,
            State = state,
            RunTime = item.RunTime,
            Network = item.Network,
            Ports = item.Ports,
            IsFromApp = item.IsFromApp,
            IsFromCompose = item.IsFromCompose,
            AppName = item.AppName,
            AppInstallName = item.AppInstallName,
            Websites = item.Websites,
            IsPinned = item.IsPinned,
            Description = item.Description
        };

        private static PanelCallResult<T> Json<T>(object value, TimeSpan elapsed) =>
            new(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions), null, elapsed);
    }
}

/// <summary>集成测试假容器日志客户端：固定返回两行日志。</summary>
public sealed class ContainerFakeLogClientFactory : IContainerLogClientFactory
{
    public IContainerLogClient Create(NodeDescriptor node) => new FakeLogClient();

    private sealed class FakeLogClient : IContainerLogClient
    {
        public Task<ContainerLogReadResult> ReadAsync(
            ContainerLogQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ContainerLogReadResult(
                ["[INFO] started", "[INFO] ready"],
                false,
                null,
                null,
                TimeSpan.FromMilliseconds(2)));

        public void Dispose()
        {
        }
    }
}
