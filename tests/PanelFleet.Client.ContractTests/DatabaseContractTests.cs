using System.Text.Json;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;
using Dbs = PanelFleet.Client.Dto.Databases;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P8 数据库契约测试：真实 <see cref="PanelClientFactory"/> + 假面板，覆盖
/// 实例/库列表映射、MySQL 用户/授权/字符集/状态、Redis CLI 检查与配置脱敏、写请求 Base64 与按引擎路由。
/// </summary>
public sealed class DatabaseContractTests
{
    [Fact]
    public async Task InstancesAndDatabases_MapFromFakePanel()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var instances = await client.GetAsync<List<Dbs.DatabaseInstanceDto>>(
            DatabaseRoutes.Instances(DatabaseRoutes.MySql));
        Assert.True(instances.IsSuccess);
        var instance = Assert.Single(instances.Data!);
        Assert.Equal(1, instance.Id);
        Assert.Equal("mysql", instance.Type);
        Assert.Equal("remote", instance.From);
        Assert.Equal("MySQL", instance.Database);
        Assert.Equal("8.x", instance.Version);

        var databases = await client.PostAsync<PagedItemsDto<Dbs.DatabaseDbDto>>(
            DatabaseRoutes.Search(DatabaseRoutes.MySql),
            new Dbs.DatabaseSearchRequest(1, 50, null, "MySQL", "createdAt", "descending"));

        Assert.True(databases.IsSuccess);
        Assert.Equal(2, databases.Data?.Total);
        var row = databases.Data!.Items![0];
        Assert.Equal("wordpress", row.Name);
        Assert.Equal("MySQL", row.InstanceName);
        Assert.Equal("utf8mb4", row.Format);
        Assert.Equal("utf8mb4_0900_ai_ci", row.Collation);
        Assert.Equal("remote", row.From);

        var body = server.GetLastRequestBody(DatabaseRoutes.Search(DatabaseRoutes.MySql));
        Assert.NotNull(body);
        Assert.Contains("\"database\":\"MySQL\"", body, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"createdAt\"", body, StringComparison.Ordinal);
        Assert.Contains("\"order\":\"descending\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyEngines_ReturnNullDataWithoutError()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var pg = await client.GetAsync<List<Dbs.DatabaseInstanceDto>>(
            DatabaseRoutes.Instances(DatabaseRoutes.PostgreSql));
        var mongo = await client.GetAsync<List<Dbs.DatabaseInstanceDto>>(
            DatabaseRoutes.Instances(DatabaseRoutes.MongoDb));

        Assert.True(pg.IsSuccess);
        Assert.Null(pg.Data);
        Assert.True(mongo.IsSuccess);
        Assert.Null(mongo.Data);
    }

    [Fact]
    public async Task UsersGrantsFormatsStatus_MapFields_AndDoNotExposePasswords()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [DatabaseRoutes.UsersSearch] = """
                    [{"username":"app","host":"%","password":"super-secret","description":"业务账号","isDelete":false}]
                    """,
                [DatabaseRoutes.GrantsSearch] = """
                    [{"database":"app","username":"app","host":"%"}]
                    """,
                [DatabaseRoutes.FormatOptions] = """
                    [{"format":"utf8mb4","collations":["utf8mb4_general_ci","utf8mb4_0900_ai_ci"]}]
                    """,
                [DatabaseRoutes.Status] = """
                    {"Threads_connected":"7","Threads_running":"1","Uptime":"3600"}
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var users = await client.PostAsync<List<Dbs.DatabaseUserDto>>(
            DatabaseRoutes.UsersSearch,
            new Dbs.DatabaseUserSearchRequest("MySQL"));
        Assert.True(users.IsSuccess);
        var user = Assert.Single(users.Data!);
        Assert.Equal("app", user.Username);
        Assert.Equal("%", user.Host);
        Assert.Equal("业务账号", user.Description);
        var userJson = JsonSerializer.Serialize(user);
        Assert.DoesNotContain("super-secret", userJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", userJson, StringComparison.Ordinal);

        var grants = await client.PostAsync<List<Dbs.DatabaseGrantDto>>(
            DatabaseRoutes.GrantsSearch,
            new Dbs.DatabaseUserSearchRequest("MySQL"));
        Assert.True(grants.IsSuccess);
        Assert.Equal("app", Assert.Single(grants.Data!).Database);

        var formats = await client.PostAsync<List<Dbs.DatabaseFormatOptionDto>>(
            DatabaseRoutes.FormatOptions,
            new { name = "MySQL" });
        Assert.True(formats.IsSuccess);
        var format = Assert.Single(formats.Data!);
        Assert.Equal("utf8mb4", format.Format);
        Assert.Equal(2, format.Collations!.Count);

        var status = await client.PostAsync<Dbs.DatabaseStatusDto>(
            DatabaseRoutes.Status,
            new { type = "mysql", name = "MySQL" });
        Assert.True(status.IsSuccess);
        Assert.Equal("7", status.Data!.ThreadsConnected);
        Assert.Equal("3600", status.Data.Uptime);
    }

    [Fact]
    public async Task Redis_CheckAndConf_DoNotExposeRequirepass()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [DatabaseRoutes.RedisCliCheck] = "false",
                [DatabaseRoutes.RedisConf] = """{"timeout":"0","maxmemory":"256mb","requirepass":"super-secret"}"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var check = await client.GetAsync<bool>(DatabaseRoutes.RedisCliCheck);
        Assert.True(check.IsSuccess);
        Assert.False(check.Data);

        var conf = await client.PostAsync<Dbs.RedisConfDto>(
            DatabaseRoutes.RedisConf,
            new { type = "redis", name = "redis" });
        Assert.True(conf.IsSuccess);
        Assert.Equal("256mb", conf.Data!.Maxmemory);
        var json = JsonSerializer.Serialize(conf.Data);
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteCheck_MapsLinkedResources()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [DatabaseRoutes.DeleteCheck(DatabaseRoutes.MySql)] = """
                    [{"type":"backup","name":"backup-20261116"}]
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<List<Dbs.DatabaseLinkedResourceDto>>(
            DatabaseRoutes.DeleteCheck(DatabaseRoutes.MySql),
            new Dbs.DatabaseDeleteCheckRequest(1, "mysql", "MySQL"));

        Assert.True(result.IsSuccess);
        var resource = Assert.Single(result.Data!);
        Assert.Equal("backup", resource.Type);
        Assert.Equal("backup-20261116", resource.Name);
    }

    [Fact]
    public async Task WriteRequests_UsePasswordJsonName_AndEngineRoutes()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [DatabaseRoutes.UsersPassword] = "{}",
                [DatabaseRoutes.Delete(DatabaseRoutes.PostgreSql)] = "{}"
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var password = await client.PostAsync<JsonElement>(
            DatabaseRoutes.UsersPassword,
            new Dbs.DatabaseUserPasswordRequest("MySQL", "app", "%", "czNjcmV0"));
        Assert.True(password.IsSuccess);
        var passwordBody = server.GetLastRequestBody(DatabaseRoutes.UsersPassword);
        Assert.NotNull(passwordBody);
        Assert.Contains("\"password\":\"czNjcmV0\"", passwordBody, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", passwordBody, StringComparison.Ordinal);

        var delete = await client.PostAsync<JsonElement>(
            DatabaseRoutes.Delete(DatabaseRoutes.PostgreSql),
            new Dbs.DatabaseDeleteRequest(9, "postgresql", "app_data", true, false));
        Assert.True(delete.IsSuccess);
        var deleteBody = server.GetLastRequestBody(DatabaseRoutes.Delete(DatabaseRoutes.PostgreSql));
        Assert.NotNull(deleteBody);
        Assert.Contains("\"type\":\"postgresql\"", deleteBody, StringComparison.Ordinal);
        Assert.Contains("\"database\":\"app_data\"", deleteBody, StringComparison.Ordinal);
        Assert.Contains("\"forceDelete\":true", deleteBody, StringComparison.Ordinal);
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

