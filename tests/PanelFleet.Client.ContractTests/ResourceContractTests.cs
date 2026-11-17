using System.Text.Json;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P4 五类只读资源契约测试：真实 <see cref="PanelClientFactory"/> + 真实 HTTP 打到假面板，
/// 验证路由、必填参数、字段映射、items:null 容错、数据库两段式与详情解析。
/// </summary>
public sealed class ResourceContractTests
{
    [Fact]
    public async Task WebsitesSearch_ReturnsTwoSites_AndRecordsRequestParameters()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<WebsiteItemDto>>(
            ResourceRoutes.WebsitesSearch,
            new { page = 1, pageSize = 50, orderBy = "created_at", order = "descending" });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data?.Total);
        Assert.Equal(2, result.Data?.Items?.Count);

        var site = result.Data!.Items![0];
        Assert.Equal(1, site.Id);
        Assert.Equal("example.com", site.PrimaryDomain);
        Assert.Equal("example.com", site.Alias);
        Assert.Equal("static", site.Type);
        Assert.Equal("Running", site.Status);
        Assert.Equal("9999-12-31T00:00:00+08:00", site.ExpireDate);
        Assert.Equal("/opt/1panel/apps/openresty/openresty/www/sites/example.com", site.SitePath);
        Assert.Equal(string.Empty, site.AppName);
        Assert.Equal(string.Empty, site.RuntimeName);

        var body = server.GetLastRequestBody(ResourceRoutes.WebsitesSearch);
        Assert.NotNull(body);
        Assert.Contains("\"orderBy\":\"created_at\"", body, StringComparison.Ordinal);
        Assert.Contains("\"order\":\"descending\"", body, StringComparison.Ordinal);
        Assert.Contains("\"page\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppsInstalledSearch_OnlyRequiresPaging_AndMapsFields()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        // 应用列表没有 orderBy/order 参数，只传 page/pageSize 应被接受。
        var result = await client.PostAsync<PagedItemsDto<AppInstalledItemDto>>(
            ResourceRoutes.AppsInstalledSearch,
            new { page = 1, pageSize = 50 });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data?.Total);
        Assert.Equal(2, result.Data?.Items?.Count);

        var app = result.Data!.Items![0];
        Assert.Equal(1, app.Id);
        Assert.Equal("mysql", app.Name);
        Assert.Equal("8.4.6", app.Version);
        Assert.Equal("Running", app.Status);
        Assert.Equal(3306, app.HttpPort);
        Assert.Equal(0, app.HttpsPort);
        Assert.Equal("/opt/1panel/apps/mysql/mysql", app.Path);
        Assert.Equal("mysql", app.AppKey);
        Assert.Equal("database", app.AppType);
    }

    [Fact]
    public async Task ContainersSearch_ReturnsTwoContainers_AndMapsFields()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<ContainerItemDto>>(
            ResourceRoutes.ContainersSearch,
            new
            {
                page = 1,
                pageSize = 50,
                name = string.Empty,
                state = "all",
                filters = string.Empty,
                orderBy = "name",
                order = "descending"
            });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data?.Total);
        Assert.Equal(2, result.Data?.Items?.Count);

        var container = result.Data!.Items![0];
        Assert.Equal("a1b2c3d4e5f6a1b2c3d4e5f6", container.ContainerID);
        Assert.Equal("mysql", container.Name);
        Assert.Equal("mysql:8.4.6", container.ImageName);
        Assert.Equal("running", container.State);
        Assert.Equal("Up 3 hours", container.RunTime);
        Assert.True(container.IsFromApp);
        Assert.False(container.IsFromCompose);
        Assert.NotNull(container.Ports);
        Assert.Equal(JsonValueKind.Array, container.Ports!.Value.ValueKind);
        Assert.NotNull(container.Network);
        Assert.Equal(JsonValueKind.Array, container.Network!.Value.ValueKind);

        var body = server.GetLastRequestBody(ResourceRoutes.ContainersSearch);
        Assert.Contains("\"state\":\"all\"", body, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"name\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CronjobsSearch_ReturnsOneCronjob_AndMapsFields()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<CronjobItemDto>>(
            ResourceRoutes.CronjobsSearch,
            new { page = 1, pageSize = 50, orderBy = "name", order = "descending" });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data?.Total);
        var job = Assert.Single(result.Data!.Items!);
        Assert.Equal(1, job.Id);
        Assert.Equal("备份网站数据", job.Name);
        Assert.Equal("backupWebsite", job.Type);
        Assert.Equal("0 3 * * *", job.Spec);
        Assert.Equal("Enable", job.Status);
        Assert.Equal("shell", job.Executor);
        Assert.Contains("tar -czf", job.Command, StringComparison.Ordinal);
        Assert.Equal(7, job.RetainCopies);
    }

    [Fact]
    public async Task Databases_TwoStepInstancesThenLibraries()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        // 第一步：实例列表。
        var instancesResult = await client.GetAsync<List<DatabaseInstanceDto>>(
            ResourceRoutes.DatabaseInstances("mysql"));

        Assert.True(instancesResult.IsSuccess);
        var instance = Assert.Single(instancesResult.Data!);
        Assert.Equal(1, instance.Id);
        Assert.Equal("MySQL", instance.Database);
        Assert.Equal("mysql", instance.Type);
        Assert.Equal("8.x", instance.Version);
        Assert.Equal("198.51.100.11", instance.Address);

        // 第二步：用实例名作为 database 参数查库列表。
        var databasesResult = await client.PostAsync<PagedItemsDto<DatabaseItemDto>>(
            ResourceRoutes.DatabaseSearch("mysql"),
            new { page = 1, pageSize = 50, database = instance.Database, orderBy = "name", order = "descending" });

        Assert.True(databasesResult.IsSuccess);
        Assert.Equal(2, databasesResult.Data?.Total);
        Assert.Equal(2, databasesResult.Data?.Items?.Count);

        var database = databasesResult.Data!.Items![0];
        Assert.Equal(1, database.Id);
        Assert.Equal("wordpress", database.Name);
        Assert.Equal("MySQL", database.MysqlName);
        Assert.Equal("remote", database.From);
        Assert.Equal("utf8mb4", database.Format);
        Assert.Equal("utf8mb4_0900_ai_ci", database.Collation);

        var body = server.GetLastRequestBody(ResourceRoutes.DatabaseSearch("mysql"));
        Assert.Contains("\"database\":\"MySQL\"", body, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"name\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Databases_UninstalledEngines_ReturnNullData_WithoutError()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        foreach (var engine in new[] { "postgresql", "mongodb" })
        {
            var result = await client.GetAsync<List<DatabaseInstanceDto>>(ResourceRoutes.DatabaseInstances(engine));

            Assert.True(result.IsSuccess);
            Assert.Null(result.Data);
        }
    }

    [Fact]
    public async Task WebsitesSearch_MissingOrderParameters_ReturnsValidationError()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<WebsiteItemDto>>(
            ResourceRoutes.WebsitesSearch,
            new { page = 1, pageSize = 20 });

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Validation, result.Error?.Kind);
        Assert.Contains("参数错误", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("orderBy", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainersSearch_MissingState_ReturnsValidationError()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<ContainerItemDto>>(
            ResourceRoutes.ContainersSearch,
            new { page = 1, pageSize = 20, orderBy = "name", order = "descending" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Validation, result.Error?.Kind);
        Assert.Contains("state", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CronjobsSearch_MissingOrderBy_ReturnsValidationError()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<CronjobItemDto>>(
            ResourceRoutes.CronjobsSearch,
            new { page = 1, pageSize = 20, order = "descending" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Validation, result.Error?.Kind);
        Assert.Contains("orderBy", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabasesSearch_MissingDatabaseInstance_ReturnsValidationError()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<DatabaseItemDto>>(
            ResourceRoutes.DatabaseSearch("mysql"),
            new { page = 1, pageSize = 20, orderBy = "name", order = "descending" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Validation, result.Error?.Kind);
        Assert.Contains("database", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmitResourceItems_ListsReturnNullItems_WithoutThrowing()
    {
        var options = new FakePanelOptions { OmitResourceItems = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var websites = await client.PostAsync<PagedItemsDto<WebsiteItemDto>>(
            ResourceRoutes.WebsitesSearch,
            new { page = 1, pageSize = 50, orderBy = "created_at", order = "descending" });
        var apps = await client.PostAsync<PagedItemsDto<AppInstalledItemDto>>(
            ResourceRoutes.AppsInstalledSearch,
            new { page = 1, pageSize = 50 });
        var containers = await client.PostAsync<PagedItemsDto<ContainerItemDto>>(
            ResourceRoutes.ContainersSearch,
            new { page = 1, pageSize = 50, state = "all", orderBy = "name", order = "descending" });
        var cronjobs = await client.PostAsync<PagedItemsDto<CronjobItemDto>>(
            ResourceRoutes.CronjobsSearch,
            new { page = 1, pageSize = 50, orderBy = "name", order = "descending" });
        var databases = await client.PostAsync<PagedItemsDto<DatabaseItemDto>>(
            ResourceRoutes.DatabaseSearch("mysql"),
            new { page = 1, pageSize = 50, database = "MySQL", orderBy = "name", order = "descending" });

        Assert.True(websites.IsSuccess);
        Assert.True(apps.IsSuccess);
        Assert.True(containers.IsSuccess);
        Assert.True(cronjobs.IsSuccess);
        Assert.True(databases.IsSuccess);
        Assert.Null(websites.Data?.Items);
        Assert.Null(apps.Data?.Items);
        Assert.Null(containers.Data?.Items);
        Assert.Null(cronjobs.Data?.Items);
        Assert.Null(databases.Data?.Items);
        Assert.Equal(2, websites.Data?.Total);
        Assert.Equal(2, databases.Data?.Total);
    }

    [Fact]
    public async Task Details_WebsiteAppContainerCronjob_AreParseable()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        // 网站：GET /websites/{id}
        var website = await client.GetAsync<JsonElement>(ResourceRoutes.WebsiteDetail(1));
        Assert.True(website.IsSuccess);
        Assert.Equal(1, website.Data.GetProperty("id").GetInt64());
        Assert.Equal("example.com", website.Data.GetProperty("primaryDomain").GetString());
        Assert.Equal(2, website.Data.GetProperty("domains").GetArrayLength());

        // 应用：GET /apps/installed/info/{id}
        var app = await client.GetAsync<JsonElement>(ResourceRoutes.AppDetail(2));
        Assert.True(app.IsSuccess);
        Assert.Equal(2, app.Data.GetProperty("id").GetInt64());
        Assert.Equal("mysql", app.Data.GetProperty("name").GetString());
        Assert.Equal(3306, app.Data.GetProperty("HttpPort").GetInt32());

        // 容器：POST /containers/info body {name}
        var container = await client.PostAsync<JsonElement>(ResourceRoutes.ContainerInfo, new { name = "mysql" });
        Assert.True(container.IsSuccess);
        Assert.Equal("mysql", container.Data.GetProperty("name").GetString());
        Assert.Equal("mysql:8.4.6", container.Data.GetProperty("image").GetString());

        // 计划任务：POST /cronjobs/load/info body {id}
        var cronjob = await client.PostAsync<JsonElement>(ResourceRoutes.CronjobInfo, new { id = 1 });
        Assert.True(cronjob.IsSuccess);
        Assert.Equal(1, cronjob.Data.GetProperty("id").GetInt64());
        Assert.Equal("备份网站数据", cronjob.Data.GetProperty("name").GetString());

        Assert.Contains("\"id\":1", server.GetLastRequestBody(ResourceRoutes.CronjobInfo), StringComparison.Ordinal);
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

        return factory.Create(new NodeDescriptor(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            options.ApiKey,
            AuthMode.HmacSha256));
    }
}
