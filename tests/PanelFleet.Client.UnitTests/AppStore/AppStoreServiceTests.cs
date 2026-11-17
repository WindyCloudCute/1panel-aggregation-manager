using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application;
using PanelFleet.Application.AppStore;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.AppStore;

/// <summary>应用商店只读服务单元测试（假 IPanelClientFactory + 假 INodeStore，不访问网络）。</summary>
public sealed class AppStoreServiceTests
{
    private const string SearchJson = """
        {"total":2,"items":[
          {"id":221,"name":"MySQL","key":"mysql","description":"Open Source Relational Database","type":"runtime","status":"","installed":false,"limit":0,"recommend":5,"gpuSupport":false,"batchInstallSupport":true,"tags":["Database"]},
          {"id":259,"name":"WordPress","key":"wordpress","description":"CMS","type":"website","status":"","installed":true,"limit":1,"recommend":52,"gpuSupport":true,"batchInstallSupport":false,"tags":["Website"]}
        ]}
        """;

    private const string TagsJson = """
        [{"id":3,"key":"Database","name":"Database","sort":1},{"id":2,"key":"Website","name":"Website","sort":2}]
        """;

    private const string AppInfoJson = """
        {"id":221,"key":"mysql","name":"MySQL","type":"runtime","icon":"mysql.png","shortDescZh":"开源关系型数据库","shortDescEn":"Open source RDBMS","website":"https://www.mysql.com","github":"https://github.com/mysql/mysql-server","document":"https://dev.mysql.com/doc/","installed":false,"versions":["8.4.13","8.4.11"],"tags":[{"id":3,"key":"Database","name":"Database"}]}
        """;

    private const string AppDetailJson = """
        {"id":2064,"appId":221,"version":"8.4.13","dockerCompose":"services:\n  mysql:\n    image: mysql:8.4.13","hostMode":false,"memoryRequired":0,"gpuSupport":false,"params":{"formFields":[
          {"type":"password","labelZh":"root用户密码","labelEn":"Root Password","envKey":"PANEL_DB_ROOT_PASSWORD","required":true,"random":true,"default":"mysql"},
          {"type":"number","labelZh":"端口","labelEn":"Port","envKey":"PANEL_APP_PORT_HTTP","required":true,"rule":"paramPort","default":3306}
        ]}}
        """;

    private const string InstalledSearchJson = """
        {"total":2,"items":[
          {"id":1,"name":"komga","appKey":"komga","appType":"website","appName":"Komga","version":"1.28.1","status":"Running","message":"","httpPort":25600,"httpsPort":0,"path":"/1panel/apps/komga/komga","container":"1Panel-komga-2pdN","serviceName":"komga","webUI":"","canUpdate":false,"isEdit":false,"linkDB":false,"favorite":false,"createdAt":"2026-09-10T18:20:38Z","env":{"PANEL_APP_PORT_HTTP":"25600"}},
          {"id":2,"name":"uptime-kuma","appKey":"uptime-kuma","appType":"website","appName":"Uptime Kuma","version":"1.23.17","status":"Running","message":"容器不存在","httpPort":9010,"httpsPort":0,"path":"/1panel/apps/uptime-kuma/uptime-kuma","container":"1Panel-uptime-kuma-p32P","serviceName":"uptime-kuma","webUI":"","canUpdate":true,"isEdit":true,"linkDB":false,"favorite":true,"createdAt":"0001-01-01T00:00:00Z","env":{}}
        ]}
        """;

    private const string InstalledParamsJson = """
        {"params":[{"key":"PANEL_APP_PORT_HTTP","labelZh":"端口","labelEn":"Port","type":"number","value":"25600","edit":true,"required":true,"rule":"paramPort","values":null},{"key":"PANEL_DB_ROOT_PASSWORD","labelZh":"root密码","labelEn":"Root Password","type":"password","value":"secret","edit":true,"required":true}],"rawCompose":"networks: {}","advanced":false,"cpuQuota":0,"memoryLimit":0,"memoryUnit":"","containerName":"1Panel-komga-2pdN","allowPort":true,"editCompose":false,"hostMode":false,"dockerCompose":"services: {}","webUI":"https://komga.local","type":"website","specifyIP":"0.0.0.0","restartPolicy":"always"}
        """;

    private const string TaskSearchJson = """
        {"total":1,"items":[{"id":"task-1","name":"安装应用 [mysql]","type":"App","operate":"TaskInstall","status":"Success","errorMsg":"","resourceID":5,"currentStep":"","createdAt":"2026-11-16T02:51:00Z","endAt":"2026-11-16T02:53:00Z"}]}
        """;

    [Fact]
    public async Task SearchAsync_MapsPage_AndSendsFilters()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/search", SearchJson);
        var (service, node) = CreateService(factory);

        var page = await service.SearchAsync(
            node.Id,
            new AppStoreQuery(Name: "mysql", Tags: ["Database"], Resource: "remote", ShowCurrentArch: true, Page: 2, PageSize: 10),
            CancellationToken.None);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.PageSize);

        var first = page.Items[0];
        Assert.Equal(221, first.Id);
        Assert.Equal("MySQL", first.Name);
        Assert.Equal("mysql", first.Key);
        Assert.Equal("runtime", first.Type);
        Assert.True(first.BatchInstallSupport);
        Assert.Equal(["Database"], first.Tags);

        var call = Assert.Single(factory.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal(AppStoreRoutes.Search, call.Path);
        Assert.NotNull(call.Body);
        Assert.Contains("\"name\":\"mysql\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"resource\":\"remote\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"showCurrentArch\":true", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"page\":2", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":10", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTagsAsync_MapsTags()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/tags", TagsJson);
        var (service, node) = CreateService(factory);

        var tags = await service.GetTagsAsync(node.Id, CancellationToken.None);

        Assert.Equal(2, tags.Count);
        Assert.Equal("Database", tags[0].Key);
        Assert.Equal("Website", tags[1].Name);
        Assert.Equal(AppStoreRoutes.Tags, Assert.Single(factory.Calls).Path);
    }

    [Fact]
    public async Task GetDetailAsync_UsesAppInfoThenVersionDetail_AndMapsFormFields()
    {
        var factory = new RecordingPanelClientFactory()
            .Respond("apps/detail/221/8.4.13/app", AppDetailJson)
            .Respond("apps/mysql", AppInfoJson);
        var (service, node) = CreateService(factory);

        var detail = await service.GetDetailAsync(node.Id, "mysql", version: null, CancellationToken.None);

        Assert.Equal(221, detail.AppId);
        Assert.Equal(2064, detail.DetailId);
        Assert.Equal("mysql", detail.Key);
        Assert.Equal("MySQL", detail.Name);
        Assert.Equal("8.4.13", detail.Version);
        Assert.Equal(["8.4.13", "8.4.11"], detail.Versions);
        Assert.Equal(2, detail.FormFields.Count);

        var password = detail.FormFields[0];
        Assert.Equal("PANEL_DB_ROOT_PASSWORD", password.EnvKey);
        Assert.Equal("root用户密码", password.LabelZh);
        Assert.Equal("password", password.Type);
        Assert.True(password.Required);
        Assert.True(password.Random);
        Assert.True(password.IsSensitive);
        Assert.Equal("mysql", password.DefaultValue);

        var port = detail.FormFields[1];
        Assert.Equal("PANEL_APP_PORT_HTTP", port.EnvKey);
        Assert.Equal("3306", port.DefaultValue);
        Assert.Equal("paramPort", port.Rule);

        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal("GET", factory.Calls[0].Method);
        Assert.Equal("GET", factory.Calls[1].Method);
        Assert.EndsWith("/app", factory.Calls[1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchInstalledAsync_MapsItems_AndSendsPaging()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/installed/search", InstalledSearchJson);
        var (service, node) = CreateService(factory);

        var page = await service.SearchInstalledAsync(
            node.Id, name: "kom", tags: null, updateOnly: true, checkUpdate: true, page: 1, pageSize: 20, CancellationToken.None);

        Assert.Equal(2, page.Total);
        var item = page.Items[0];
        Assert.Equal(1, item.Id);
        Assert.Equal("komga", item.Name);
        Assert.Equal("komga", item.AppKey);
        Assert.Equal("1.28.1", item.Version);
        Assert.Equal(25600, item.HttpPort);
        Assert.Equal("25600", item.Env["PANEL_APP_PORT_HTTP"]);
        Assert.NotNull(item.CreatedAt);
        Assert.True(page.Items[1].CanUpdate);
        Assert.True(item.LinkDB == false);

        var body = Assert.Single(factory.Calls).Body;
        Assert.NotNull(body);
        Assert.Contains("\"update\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"checkUpdate\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"kom\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInstalledParamsAsync_MapsParams_AndFlagsSensitive()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/installed/params/1", InstalledParamsJson);
        var (service, node) = CreateService(factory);

        var config = await service.GetInstalledParamsAsync(node.Id, 1, CancellationToken.None);

        Assert.Equal(2, config.Params.Count);
        Assert.Equal("25600", config.Params[0].Value);
        Assert.Equal("paramPort", config.Params[0].Rule);
        Assert.True(config.Params[1].IsSensitive);
        Assert.True(config.AllowPort);
        Assert.Equal("always", config.RestartPolicy);
        Assert.Equal("https://komga.local", config.WebUI);
    }

    [Fact]
    public async Task GetUpdateVersionsAsync_NullData_ReturnsEmpty()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/installed/update/versions", "null");
        var (service, node) = CreateService(factory);

        var versions = await service.GetUpdateVersionsAsync(node.Id, 2, CancellationToken.None);

        Assert.Empty(versions);
        Assert.Contains("\"appInstallID\":2", Assert.Single(factory.Calls).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUpdateVersionsAsync_SortsAscending_NewestIsLast()
    {
        const string versionsJson = """
            [{"version":"1.10.0","detailId":31,"dockerCompose":""},{"version":"1.2.0","detailId":12,"dockerCompose":""},{"version":"1.9.1","detailId":19,"dockerCompose":""}]
            """;
        var factory = new RecordingPanelClientFactory().Respond("apps/installed/update/versions", versionsJson);
        var (service, node) = CreateService(factory);

        var versions = await service.GetUpdateVersionsAsync(node.Id, 2, CancellationToken.None);

        Assert.Equal(["1.2.0", "1.9.1", "1.10.0"], versions.Select(version => version.Version));
        Assert.Equal(31, versions[^1].DetailId);
    }

    [Fact]
    public async Task GetDeleteCheckAsync_NullData_ReturnsEmpty()
    {
        var factory = new RecordingPanelClientFactory().Respond("apps/installed/delete/check/2", "null");
        var (service, node) = CreateService(factory);

        var resources = await service.GetDeleteCheckAsync(node.Id, 2, CancellationToken.None);

        Assert.Empty(resources);
    }

    [Fact]
    public async Task GetTaskStatusAsync_MapsTask_AndReturnsNullWhenMissing()
    {
        var factory = new RecordingPanelClientFactory().Respond("logs/tasks/search", TaskSearchJson);
        var (service, node) = CreateService(factory);

        var status = await service.GetTaskStatusAsync(node.Id, "task-1", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Success", status.Status);
        Assert.True(status.IsSucceeded);
        Assert.False(status.IsExecuting);
        Assert.NotNull(status.FinishedAt);

        var missing = await service.GetTaskStatusAsync(node.Id, "task-unknown", CancellationToken.None);
        Assert.Null(missing);
    }

    [Fact]
    public async Task UnknownNode_ThrowsNodeNotFound()
    {
        var (service, _) = CreateService(new RecordingPanelClientFactory());

        await Assert.ThrowsAsync<NodeNotFoundException>(
            () => service.SearchAsync(NodeId.New(), new AppStoreQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task DisabledNode_ThrowsAppStoreException()
    {
        var node = AppStoreTestData.Node("disabled", enabled: false);
        var store = new AppStoreFakeNodeStore(node);
        var service = new AppStoreService(
            store,
            new AppStoreFakeProtector(),
            new RecordingPanelClientFactory(),
            NullLogger<AppStoreService>.Instance);

        var exception = await Assert.ThrowsAsync<AppStoreException>(
            () => service.GetTagsAsync(node.Id, CancellationToken.None));
        Assert.Contains("已禁用", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PanelError_ThrowsAppStoreExceptionWithKind()
    {
        var factory = new RecordingPanelClientFactory().Fail("apps/search", PanelErrorKind.Unauthorized);
        var (service, node) = CreateService(factory);

        var exception = await Assert.ThrowsAsync<AppStoreException>(
            () => service.SearchAsync(node.Id, new AppStoreQuery(), CancellationToken.None));

        Assert.Equal(PanelErrorKind.Unauthorized, exception.Error.Kind);
    }

    private static (AppStoreService Service, Application.Models.NodeRecord Node) CreateService(RecordingPanelClientFactory factory)
    {
        var node = AppStoreTestData.Node();
        var service = new AppStoreService(
            new AppStoreFakeNodeStore(node),
            new AppStoreFakeProtector(),
            factory,
            NullLogger<AppStoreService>.Instance);
        return (service, node);
    }
}
