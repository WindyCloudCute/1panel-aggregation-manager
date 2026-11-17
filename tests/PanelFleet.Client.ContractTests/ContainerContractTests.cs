using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;
using Containers = PanelFleet.Client.Dto.Containers;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P6 容器契约测试：真实 <see cref="PanelClientFactory"/> + 真实 HTTP 打到假面板，
/// 覆盖容器列表/统计/Compose 列表/inspect 与 SSE 日志读取。
/// </summary>
public sealed class ContainerContractTests
{
    private const string ListStatsRoute = "api/v2/containers/list/stats";

    private const string ComposeSearchRoute = "api/v2/containers/compose/search";

    private const string InspectRoute = "api/v2/containers/inspect";

    private const string LogRoute = "api/v2/containers/search/log";

    [Fact]
    public async Task ContainersSearch_MapsFields_AndSendsRequiredParameters()
    {
        // 容器 search 路由由共享假面板 FakePanelServer 已实现（并校验必填参数），这里直接复用。
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<Containers.ContainerItemDto>>(
            ContainerRoutes.Search,
            new Containers.ContainerSearchRequest(1, 50, null, "all", string.Empty, "createdAt", "descending"));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data?.Total);
        var first = result.Data!.Items![0];
        Assert.Equal("mysql", first.Name);
        Assert.Equal("mysql:8.4.6", first.ImageName);
        Assert.Equal("running", first.State);
        Assert.Equal("0.0.0.0:3306->3306/tcp", first.Ports![0]);
        Assert.Equal("172.17.0.2", first.Network![0]);
        Assert.True(first.IsFromApp);
        Assert.Equal("mysql", first.AppName);

        var second = result.Data.Items[1];
        Assert.Equal("uptime-kuma", second.Name);
        Assert.Equal("exited", second.State);
        Assert.True(second.IsFromCompose);
        Assert.True(second.IsPinned);

        var body = server.GetLastRequestBody(ContainerRoutes.Search);
        Assert.NotNull(body);
        Assert.Contains("\"state\":\"all\"", body, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"createdAt\"", body, StringComparison.Ordinal);
        Assert.Contains("\"order\":\"descending\"", body, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerListStats_ParsesArrayAndRuntimeFields()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [ListStatsRoute] = """
                    [{"containerID":"aaa111","cpuTotalUsage":100,"systemUsage":1000,"cpuPercent":1.5,"percpuUsage":0,"memoryCache":0,"memoryUsage":1048576,"memoryLimit":2147483648,"memoryPercent":2.5}]
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.GetAsync<List<Containers.ContainerListStatsDto>>(ContainerRoutes.ListStats);

        Assert.True(result.IsSuccess);
        var stats = Assert.Single(result.Data!);
        Assert.Equal("aaa111", stats.ContainerID);
        Assert.Equal(1.5, stats.CpuPercent, 3);
        Assert.Equal(1048576, stats.MemoryUsage);
    }

    [Fact]
    public async Task ComposeSearch_MapsProject_AndDoesNotExposeEnv()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [ComposeSearchRoute] = """
                    {"total":1,"items":[
                      {"name":"webstack","createdAt":"2026-11-10 10:00:00","createdBy":"1Panel","containerCount":2,"runningCount":1,"configFile":"/opt/webstack/docker-compose.yml","workdir":"/opt/webstack","composeFileExists":true,"isPinned":false,"path":"/opt/webstack/docker-compose.yml","containers":[{"containerID":"aaa111","name":"web","createTime":"2026-11-10 10:00:00","state":"running","ports":["0.0.0.0:8080->80/tcp"]}],"env":"MYSQL_ROOT_PASSWORD=super-secret"}
                    ]}
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<Containers.ComposeItemDto>>(
            ContainerRoutes.ComposeSearch,
            new Containers.ComposeSearchRequest(1, 50, null, false));

        Assert.True(result.IsSuccess);
        var project = Assert.Single(result.Data!.Items!);
        Assert.Equal("webstack", project.Name);
        Assert.Equal(2, project.ContainerCount);
        Assert.Equal(1, project.RunningCount);
        Assert.True(project.ComposeFileExists);
        Assert.Equal("/opt/webstack/docker-compose.yml", project.Path);
        Assert.Equal("web", project.Containers![0].Name);

        // ComposeItemDto 不映射 env，序列化 DTO 不应该出现 1Panel 返回的明文密码。
        var serialized = JsonSerializer.Serialize(project);
        Assert.DoesNotContain("super-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_ReturnsRawDockerJsonString()
    {
        const string inspectJson = """{"Id":"aaa111","Name":"/web","State":{"Status":"running"}}""";
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [InspectRoute] = JsonSerializer.Serialize(inspectJson)
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<JsonElement>(
            ContainerRoutes.Inspect,
            new Containers.ContainerInspectRequest("aaa111", "container"));

        Assert.True(result.IsSuccess);
        Assert.Equal(JsonValueKind.String, result.Data.ValueKind);
        var raw = result.Data.GetString();
        Assert.NotNull(raw);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal("running", document.RootElement.GetProperty("State").GetProperty("Status").GetString());
    }

    [Fact]
    public async Task ContainerLogClient_ReadsSseDataLines()
    {
        var options = new FakePanelOptions
        {
            RawJsonRoutes = new Dictionary<string, string>
            {
                [LogRoute] = "data: line-1\n\ndata: line-2\n\n"
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var factory = new ContainerLogClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(5), ConnectTimeout = TimeSpan.FromSeconds(2) },
            TimeProvider.System,
            NullLoggerFactory.Instance);
        using var logClient = factory.Create(CreateNode(server, options));

        var result = await logClient.ReadAsync(
            new ContainerLogQuery("web", Compose: null, "all", 100, Timestamp: false),
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(["line-1", "line-2"], result.Lines);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ContainerLogClient_MapsSseErrorEvent()
    {
        var options = new FakePanelOptions
        {
            RawJsonRoutes = new Dictionary<string, string>
            {
                [LogRoute] = "event: error\ndata: container not found\n\n"
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var factory = new ContainerLogClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(5), ConnectTimeout = TimeSpan.FromSeconds(2) },
            TimeProvider.System,
            NullLoggerFactory.Instance);
        using var logClient = factory.Create(CreateNode(server, options));

        var result = await logClient.ReadAsync(
            new ContainerLogQuery("missing", Compose: null, "all", 100, Timestamp: false),
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("container not found", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeCreate_SendsTaskIdNameEditSourceFileAndForcePull()
    {
        const string route = "api/v2/containers/compose";
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?> { [route] = "{}" }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<JsonElement>(
            ContainerRoutes.ComposeCreate,
            new Containers.ComposeCreateRequest(
                "task-123",
                "webstack",
                "webstack",
                "edit",
                "services: {}",
                null,
                null,
                ForcePull: false));

        Assert.True(result.IsSuccess);
        var body = server.GetLastRequestBody(route);
        Assert.NotNull(body);
        Assert.Contains("\"taskID\":\"task-123\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"webstack\"", body, StringComparison.Ordinal);
        Assert.Contains("\"from\":\"edit\"", body, StringComparison.Ordinal);
        Assert.Contains("\"file\":\"services: {}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"forcePull\":false", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeUpdate_SendsDetailPathContentAndForcePull()
    {
        const string route = "api/v2/containers/compose/update";
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?> { [route] = "{}" }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<JsonElement>(
            ContainerRoutes.ComposeUpdate,
            new Containers.ComposeUpdateRequest(
                "task-9",
                "webstack",
                "/opt/webstack",
                "/opt/webstack/docker-compose.yml",
                "services: {}",
                null,
                ForcePull: true));

        Assert.True(result.IsSuccess);
        var body = server.GetLastRequestBody(route);
        Assert.NotNull(body);
        Assert.Contains("\"taskID\":\"task-9\"", body, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"/opt/webstack\"", body, StringComparison.Ordinal);
        Assert.Contains("\"detailPath\":\"/opt/webstack/docker-compose.yml\"", body, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"services: {}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"forcePull\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileContent_PostsPathAndIsDetail_AndParsesFlatFileInfo()
    {
        const string route = "api/v2/files/content";
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [route] = """
                    {"content":"services: {}","size":13,"path":"/opt/webstack/docker-compose.yml","name":"docker-compose.yml","mimeType":"text/yaml","extension":".yml","isDir":false}
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<JsonElement>(
            ContainerRoutes.FileContent,
            new Containers.FileContentRequest("/opt/webstack/docker-compose.yml", false));

        Assert.True(result.IsSuccess);
        Assert.Equal(JsonValueKind.Object, result.Data.ValueKind);
        Assert.Equal("services: {}", result.Data.GetProperty("content").GetString());
        Assert.False(result.Data.GetProperty("isDir").GetBoolean());

        var body = server.GetLastRequestBody(route);
        Assert.NotNull(body);
        Assert.Contains("\"path\":\"/opt/webstack/docker-compose.yml\"", body, StringComparison.Ordinal);
        Assert.Contains("\"isDetail\":false", body, StringComparison.Ordinal);
    }

    private static IPanelClient CreateClient(FakePanelServer server, FakePanelOptions options)
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(2)
            },
            TimeProvider.System);

        return factory.Create(CreateNode(server, options));
    }

    private static NodeDescriptor CreateNode(FakePanelServer server, FakePanelOptions options) => new(
        NodeId.New(),
        "node-1",
        server.BaseUri,
        options.ApiKey,
        AuthMode.HmacSha256);
}
