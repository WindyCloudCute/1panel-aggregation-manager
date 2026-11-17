using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.AppStore;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.AppStore;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P5 应用商店契约测试：真实 <see cref="PanelClientFactory"/> 打到进程内假 1Panel，
/// 验证路由、请求体、DTO 字段映射、宽松解析与业务错误映射。
/// </summary>
public sealed class AppStoreContractTests
{
    private const string SearchJson = """
        {"total":2,"items":[
          {"id":221,"name":"MySQL","key":"mysql","description":"Open Source Relational Database","type":"runtime","status":"","installed":false,"limit":0,"recommend":5,"gpuSupport":false,"batchInstallSupport":true,"tags":["Database"]},
          {"id":259,"name":"WordPress","key":"wordpress","description":"CMS","type":"website","status":"","installed":true,"limit":1,"recommend":52,"gpuSupport":true,"batchInstallSupport":false,"tags":["Website"]}
        ]}
        """;

    private const string TagsJson = """[{"id":3,"key":"Database","name":"Database"},{"id":2,"key":"Website","name":"Website"}]""";

    private const string AppInfoJson = """
        {"id":221,"key":"mysql","name":"MySQL","type":"runtime","icon":"mysql.png","shortDescZh":"开源关系型数据库","shortDescEn":"Open source RDBMS","website":"https://www.mysql.com","github":"https://github.com/mysql/mysql-server","document":"https://dev.mysql.com/doc/","installed":false,"versions":["8.4.13","8.4.11"],"tags":[{"id":3,"key":"Database","name":"Database"}]}
        """;

    private const string AppDetailJson = """
        {"id":2064,"appId":221,"version":"8.4.13","dockerCompose":"services:\n  mysql:\n    image: mysql:8.4.13","hostMode":false,"memoryRequired":0,"gpuSupport":false,"params":{"formFields":[
          {"type":"password","labelZh":"root用户密码","labelEn":"Root Password","envKey":"PANEL_DB_ROOT_PASSWORD","required":true,"random":true,"default":"mysql"},
          {"type":"number","labelZh":"端口","labelEn":"Port","envKey":"PANEL_APP_PORT_HTTP","required":true,"rule":"paramPort","default":3306},
          {"type":"select","labelZh":"时区","labelEn":"Timezone","envKey":"TZ","required":false,"values":[{"label":"Asia/Shanghai","value":"Asia/Shanghai"},{"label":"UTC","value":"UTC"}]}
        ]}}
        """;

    private const string InstalledParamsJson = """
        {"params":[{"key":"PANEL_APP_PORT_HTTP","labelZh":"端口","labelEn":"Port","type":"number","value":"25600","edit":true,"required":true,"rule":"paramPort"},{"key":"PANEL_DB_ROOT_PASSWORD","labelZh":"root密码","labelEn":"Root Password","type":"password","value":"secret","edit":true,"required":true}],"rawCompose":"","advanced":false,"cpuQuota":0,"memoryLimit":0,"memoryUnit":"M","containerName":"1Panel-komga-2pdN","allowPort":true,"editCompose":false,"hostMode":false,"pullImage":false,"gpuConfig":false,"dockerCompose":"services: {}","webUI":"","type":"website","specifyIP":"","restartPolicy":"always"}
        """;

    private const string TaskSearchJson = """
        {"total":1,"items":[{"id":"task-1","name":"安装应用 [mysql]","type":"App","operate":"TaskInstall","status":"Executing","errorMsg":"","resourceID":5,"currentStep":"","createdAt":"2026-11-16T02:51:00Z","endAt":""}]}
        """;

    private const string UpdateVersionsJson = """
        [{"version":"1.29.0","detailId":9001,"dockerCompose":"services: {}"}]
        """;

    [Fact]
    public async Task Search_MapsItems_AndSendsFilters()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?> { [AppStoreRoutes.Search] = SearchJson }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.PostAsync<PagedItemsDto<AppStoreItemDto>>(
            AppStoreRoutes.Search,
            new { page = 1, pageSize = 10, name = "", tags = new[] { "Database" }, resource = "remote", showCurrentArch = true });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data?.Total);
        var item = result.Data!.Items![0];
        Assert.Equal(221, item.Id);
        Assert.Equal("mysql", item.Key);
        Assert.Equal("runtime", item.Type);
        Assert.True(item.BatchInstallSupport);
        Assert.Equal(new[] { "Database" }, item.Tags);

        var body = server.GetLastRequestBody(AppStoreRoutes.Search);
        Assert.NotNull(body);
        Assert.Contains("\"showCurrentArch\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"resource\":\"remote\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tags_AppInfo_And_AppDetail_AreMapped()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [AppStoreRoutes.Tags] = TagsJson,
                [AppStoreRoutes.App("mysql")] = AppInfoJson,
                [AppStoreRoutes.AppDetail(221, "8.4.13")] = AppDetailJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var tags = await client.GetAsync<List<AppTagDto>>(AppStoreRoutes.Tags);
        Assert.True(tags.IsSuccess);
        Assert.Equal("Database", tags.Data![0].Key);

        var app = await client.GetAsync<AppInfoDto>(AppStoreRoutes.App("mysql"));
        Assert.True(app.IsSuccess);
        Assert.Equal(221, app.Data?.Id);
        Assert.Equal(new[] { "8.4.13", "8.4.11" }, app.Data!.Versions);

        var detail = await client.GetAsync<AppDetailDto>(AppStoreRoutes.AppDetail(221, "8.4.13"));
        Assert.True(detail.IsSuccess);
        Assert.Equal(2064, detail.Data?.Id);
        var fields = detail.Data!.Params!.FormFields!;
        Assert.Equal(3, fields.Count);
        Assert.Equal("PANEL_APP_PORT_HTTP", fields[1].EnvKey);
        Assert.Equal(3306, fields[1].Default!.Value.GetInt32());
        Assert.Equal(2, fields[2].Values!.Count);
    }

    [Fact]
    public async Task InstalledSearch_Info_And_Params_AreMapped()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [AppStoreRoutes.InstalledParams(1)] = InstalledParamsJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        // 已安装列表 / 详情使用 FakePanelServer 内置的 P4 资源路由（添加同名路由会与内置路由冲突）。
        var page = await client.PostAsync<PagedItemsDto<InstalledAppItemDto>>(
            AppStoreRoutes.InstalledSearch,
            new { page = 1, pageSize = 20 });
        Assert.True(page.IsSuccess);
        Assert.Equal(2, page.Data?.Total);
        Assert.Equal("mysql", page.Data!.Items![0].Name);
        Assert.Equal(3306, page.Data.Items[0].HttpPort);

        var info = await client.GetAsync<InstalledAppInfoDto>(AppStoreRoutes.InstalledInfo(1));
        Assert.True(info.IsSuccess);
        Assert.Equal(3306, info.Data?.HttpPort);
        Assert.Equal(new[] { 3306 }, info.Data!.AppPorts);

        var config = await client.GetAsync<InstalledAppConfigDto>(AppStoreRoutes.InstalledParams(1));
        Assert.True(config.IsSuccess);
        Assert.Equal(2, config.Data!.Params!.Count);
        Assert.Equal("password", config.Data.Params[1].Type);
    }

    [Fact]
    public async Task UpdateVersions_And_TaskSearch_AreMapped()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [AppStoreRoutes.InstalledUpdateVersions] = UpdateVersionsJson,
                [AppStoreRoutes.TaskSearch] = TaskSearchJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var versions = await client.PostAsync<List<AppUpdateVersionDto>>(
            AppStoreRoutes.InstalledUpdateVersions,
            new { appInstallID = 1 });
        Assert.True(versions.IsSuccess);
        Assert.Equal(9001, versions.Data![0].DetailId);
        Assert.Contains(
            "\"appInstallID\":1",
            server.GetLastRequestBody(AppStoreRoutes.InstalledUpdateVersions),
            StringComparison.Ordinal);

        var tasks = await client.PostAsync<PagedItemsDto<PanelTaskDto>>(
            AppStoreRoutes.TaskSearch,
            new { page = 1, pageSize = 20, taskID = "task-1" });
        Assert.True(tasks.IsSuccess);
        Assert.Equal("Executing", tasks.Data!.Items![0].Status);
    }

    [Fact]
    public async Task WriteRoutes_AcceptBodies_WithoutUsingRealPanel()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [AppStoreRoutes.Install] = """{"id":5,"name":"mysql"}""",
                [AppStoreRoutes.InstalledOperate] = """{}""",
                [AppStoreRoutes.InstalledParamsUpdate] = """{}"""
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var install = await client.PostAsync<JsonElement>(
            AppStoreRoutes.Install,
            new { appDetailId = 2064, name = "mysql", @params = new { PANEL_PORT = 3306 }, pullImage = true, taskID = "t-1" });
        Assert.True(install.IsSuccess);
        Assert.Contains("\"appDetailId\":2064", server.GetLastRequestBody(AppStoreRoutes.Install), StringComparison.Ordinal);

        var uninstall = await client.PostAsync<JsonElement>(
            AppStoreRoutes.InstalledOperate,
            new { operate = "delete", installId = 7, deleteDB = false, taskID = "t-2" });
        Assert.True(uninstall.IsSuccess);
        Assert.Contains("\"operate\":\"delete\"", server.GetLastRequestBody(AppStoreRoutes.InstalledOperate), StringComparison.Ordinal);

        var update = await client.PostAsync<JsonElement>(
            AppStoreRoutes.InstalledParamsUpdate,
            new { installId = 7, @params = new { PANEL_PORT = "8080" }, advanced = false });
        Assert.True(update.IsSuccess);
        Assert.Contains("\"installId\":7", server.GetLastRequestBody(AppStoreRoutes.InstalledParamsUpdate), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusinessError_MapsToValidationError()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            RawJsonRoutes = new Dictionary<string, string>
            {
                [AppStoreRoutes.Install] = """{"code":400,"message":"应用版本不存在","data":null}"""
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.PostAsync<JsonElement>(AppStoreRoutes.Install, new { appDetailId = 1, name = "x" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Validation, result.Error?.Kind);
        Assert.Contains("应用版本不存在", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceAgainstFakePanel_TwoStepDetail_AndTaskStatus()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [AppStoreRoutes.App("mysql")] = AppInfoJson,
                [AppStoreRoutes.AppDetail(221, "8.4.13")] = AppDetailJson,
                [AppStoreRoutes.TaskSearch] = TaskSearchJson
            }
        });
        await server.StartAsync();

        var node = new NodeRecord(
            NodeId.New(),
            "node-1",
            server.BaseUri,
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
            true,
            "v2.3.2",
            0,
            NodeHealth.Online,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var factory = new PanelClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(3) },
            TimeProvider.System);
        var service = new AppStoreService(
            new SingleNodeStore(node),
            new FakeProtector(),
            factory,
            NullLogger<AppStoreService>.Instance);

        var detail = await service.GetDetailAsync(node.Id, "mysql", "8.4.13", CancellationToken.None);
        Assert.Equal(2064, detail.DetailId);
        Assert.Equal(3, detail.FormFields.Count);
        Assert.Equal("3306", detail.FormFields[1].DefaultValue);

        var task = await service.GetTaskStatusAsync(node.Id, "task-1", CancellationToken.None);
        Assert.NotNull(task);
        Assert.True(task.IsExecuting);
    }

    [Fact]
    public void Routes_AreStable()
    {
        Assert.Equal("api/v2/apps/search", AppStoreRoutes.Search);
        Assert.Equal("api/v2/apps/tags", AppStoreRoutes.Tags);
        Assert.Equal("api/v2/apps/checkupdate", AppStoreRoutes.CheckUpdate);
        Assert.Equal("api/v2/apps/install", AppStoreRoutes.Install);
        Assert.Equal("api/v2/apps/installed/search", AppStoreRoutes.InstalledSearch);
        Assert.Equal("api/v2/apps/installed/op", AppStoreRoutes.InstalledOperate);
        Assert.Equal("api/v2/apps/installed/params/update", AppStoreRoutes.InstalledParamsUpdate);
        Assert.Equal("api/v2/apps/installed/update/versions", AppStoreRoutes.InstalledUpdateVersions);
        Assert.Equal("api/v2/logs/tasks/search", AppStoreRoutes.TaskSearch);
        Assert.Equal("api/v2/apps/mysql", AppStoreRoutes.App("mysql"));
        Assert.Equal("api/v2/apps/detail/221/8.4.13/app", AppStoreRoutes.AppDetail(221, "8.4.13"));
        Assert.Equal("api/v2/apps/installed/info/1", AppStoreRoutes.InstalledInfo(1));
        Assert.Equal("api/v2/apps/installed/params/1", AppStoreRoutes.InstalledParams(1));
        Assert.Equal("api/v2/apps/installed/delete/check/1", AppStoreRoutes.InstalledDeleteCheck(1));
    }

    private static IPanelClient CreateClient(FakePanelServer server)
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(3) },
            TimeProvider.System);

        return factory.Create(new NodeDescriptor(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            "test-api-key",
            AuthMode.HmacSha256));
    }

    private sealed class SingleNodeStore : INodeStore
    {
        private readonly NodeRecord _node;

        public SingleNodeStore(NodeRecord node) => _node = node;

        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NodeRecord>>([_node]);

        public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NodeId>>([_node.Id]);

        public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
            Task.FromResult<NodeRecord?>(_node.Id == id ? _node : null);

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
        public ProtectedSecret Protect(string apiKey) => new("cipher", "hint");

        public string Unprotect(string cipherText) => "test-api-key";
    }
}
