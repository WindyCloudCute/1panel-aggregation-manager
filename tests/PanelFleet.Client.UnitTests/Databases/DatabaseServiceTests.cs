using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Databases;
using PanelFleet.Client.UnitTests.Containers;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Databases;

public sealed class DatabaseServiceTests
{
    private const string InstancesMySqlJson = """
        [{"id":1,"type":"mysql","from":"remote","database":"MySQL","version":"8.x","address":"192.0.2.10"}]
        """;

    private const string DatabasesJson = """
        {"total":2,"items":[
          {"id":1,"createdAt":"2026-10-05T02:54:40Z","name":"app","from":"remote","mysqlName":"MySQL","format":"utf8mb4","collation":"utf8mb4_general_ci","username":"","password":"super-secret","permission":"","isDelete":false,"description":"业务库"},
          {"id":2,"createdAt":"2026-10-05T02:54:41Z","name":"report","from":"remote","mysqlName":"MySQL","format":"utf8mb4","collation":"utf8mb4_0900_ai_ci","isDelete":false,"description":""}
        ]}
        """;

    private const string UsersJson = """
        [{"username":"app","host":"%","password":"super-secret","description":"业务账号","isDelete":false}]
        """;

    private const string GrantsJson = """
        [{"database":"app","username":"app","host":"%"}]
        """;

    private const string FormatsJson = """
        [{"format":"utf8mb4","collations":["utf8mb4_general_ci","utf8mb4_0900_ai_ci"]}]
        """;

    [Fact]
    public async Task GetOverviewAsync_MergesInstancesAndDatabases_WithoutSecrets()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = CreateOverviewFactory();
        var service = CreateService([node], factory);

        var overview = await service.GetOverviewAsync(null, CancellationToken.None);

        var group = Assert.Single(overview.Nodes);
        Assert.Null(group.Error);
        Assert.Equal("node-a", group.NodeName);
        var instance = Assert.Single(group.Instances);
        Assert.Equal(DatabaseEngine.MySql, instance.Engine);
        Assert.Equal("MySQL", instance.Instance);
        Assert.Equal("remote", instance.From);
        Assert.Equal(2, instance.DatabaseTotal);
        Assert.Equal(2, instance.Databases.Count);
        var app = instance.Databases.Single(db => db.Name == "app");
        Assert.Equal("utf8mb4", app.Format);
        Assert.Equal("utf8mb4_general_ci", app.Collation);
        Assert.Equal("业务库", app.Description);
        Assert.NotNull(app.CreatedAt);

        var serialized = System.Text.Json.JsonSerializer.Serialize(overview);
        Assert.DoesNotContain("super-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOverviewAsync_NodeFailure_IsIsolated()
    {
        var good = ContainerTestData.Node("node-a");
        var bad = ContainerTestData.Node("bad-node");
        var factory = CreateOverviewFactory()
            .Fail("bad-node", new PanelError(PanelErrorKind.Unauthorized, "bad key"));
        var service = CreateService([good, bad], factory);

        var overview = await service.GetOverviewAsync(null, CancellationToken.None);

        Assert.Equal(2, overview.Nodes.Count);
        var goodGroup = overview.Nodes.Single(node => node.NodeName == "node-a");
        Assert.Single(goodGroup.Instances);
        var badGroup = overview.Nodes.Single(node => node.NodeName == "bad-node");
        Assert.Empty(badGroup.Instances);
        Assert.NotNull(badGroup.Error);
        Assert.Contains(badGroup.Warnings, warning => warning.Contains("失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetUsersGrantsFormatsAsync_MapFields()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory()
            .Map("databases/users/search", UsersJson)
            .Map("databases/grants/search", GrantsJson)
            .Map("databases/format/options", FormatsJson);
        var service = CreateService([node], factory);

        var users = await service.GetUsersAsync(node.Id.Value, "MySQL", CancellationToken.None);
        var grants = await service.GetGrantsAsync(node.Id.Value, "MySQL", CancellationToken.None);
        var formats = await service.GetFormatsAsync(node.Id.Value, "MySQL", CancellationToken.None);

        Assert.Null(users.Error);
        var user = Assert.Single(users.Items);
        Assert.Equal("app", user.Username);
        Assert.Equal("%", user.Host);
        Assert.Equal("业务账号", user.Description);

        Assert.Null(grants.Error);
        var grant = Assert.Single(grants.Items);
        Assert.Equal("app", grant.Db);
        Assert.Equal("app", grant.Username);

        Assert.Null(formats.Error);
        var format = Assert.Single(formats.Items);
        Assert.Equal("utf8mb4", format.Format);
        Assert.Equal(2, format.Collations.Count);
    }

    [Fact]
    public async Task GetUsersAsync_PanelError_IsReturnedNotThrown()
    {
        var node = ContainerTestData.Node("node-a");
        var service = CreateService([node], new FakePanelClientFactory());

        var result = await service.GetUsersAsync(node.Id.Value, "MySQL", CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.NotNull(result.Error);
        Assert.Equal(PanelErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task CheckDeleteAsync_MapsLinkedResources()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory()
            .Map("databases/del/check", """[{"type":"backup","name":"backup-20261116"}]""");
        var service = CreateService([node], factory);

        var result = await service.CheckDeleteAsync(
            node.Id.Value,
            DatabaseEngine.MySql,
            1,
            "MySQL",
            CancellationToken.None);

        Assert.Null(result.Error);
        var resource = Assert.Single(result.Items);
        Assert.Equal("backup", resource.Type);
        Assert.Equal("backup-20261116", resource.Name);
    }

    [Fact]
    public async Task GetStatusAsync_RemoteInstance_ReturnsUnsupportedWithoutPanelCall()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory();
        var service = CreateService([node], factory);

        var status = await service.GetStatusAsync(
            node.Id.Value,
            DatabaseEngine.MySql,
            "MySQL",
            "remote",
            CancellationToken.None);

        Assert.False(status.Supported);
        Assert.Contains("远程实例", status.UnsupportedReason, StringComparison.Ordinal);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task GetStatusAsync_LocalInstance_ParsesMetrics()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory()
            .Map("databases/status", """
                {"Threads_connected":"7","Threads_running":"1","Max_used_connections":"12","Uptime":"3600","Questions":"1234","Bytes_received":"10","Bytes_sent":"20","Slow_queries":"0"}
                """);
        var service = CreateService([node], factory);

        var status = await service.GetStatusAsync(
            node.Id.Value,
            DatabaseEngine.MySql,
            "MySQL",
            "local",
            CancellationToken.None);

        Assert.True(status.Supported);
        Assert.Equal("7", status.ThreadsConnected);
        Assert.Equal("3600", status.Uptime);
        Assert.Equal("1234", status.Questions);
    }

    [Fact]
    public async Task GetRedisInfoAsync_RemoteOrCliMissing_IsUnsupported()
    {
        var node = ContainerTestData.Node("node-a");
        var service = CreateService([node], new FakePanelClientFactory());

        var remote = await service.GetRedisInfoAsync(node.Id.Value, "redis", "remote", CancellationToken.None);
        Assert.False(remote.Supported);
        Assert.Contains("远程实例", remote.UnsupportedReason, StringComparison.Ordinal);

        var noCliFactory = new FakePanelClientFactory().Map("databases/redis/check", "false");
        var local = await CreateService([node], noCliFactory)
            .GetRedisInfoAsync(node.Id.Value, "redis", "local", CancellationToken.None);
        Assert.False(local.Supported);
        Assert.Contains("Redis CLI", local.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRedisInfoAsync_Local_ReturnsStatusAndConf()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory()
            .Map("databases/redis/check", "true")
            .Map("databases/redis/status", """{"tcp_port":"6379","uptime_in_days":"3","connected_clients":"2","used_memory":"1024"}""")
            .Map("databases/redis/conf", """{"timeout":"0","maxclients":"10000","maxmemory":"256mb","requirepass":"super-secret"}""")
            .Map("databases/redis/persistence/conf", """{"appendonly":"yes","appendfsync":"everysec"}""");
        var service = CreateService([node], factory);

        var redis = await service.GetRedisInfoAsync(node.Id.Value, "redis", "local", CancellationToken.None);

        Assert.True(redis.Supported);
        Assert.Equal("6379", redis.Status?.TcpPort);
        Assert.Equal("256mb", redis.Conf?.Maxmemory);
        Assert.Equal("everysec", redis.Conf?.Appendfsync);
        Assert.DoesNotContain("super-secret", System.Text.Json.JsonSerializer.Serialize(redis), StringComparison.Ordinal);
    }

    private static FakePanelClientFactory CreateOverviewFactory() =>
        new FakePanelClientFactory()
            .Map("databases/db/list/mysql", InstancesMySqlJson)
            .Map("databases/db/list/postgresql", "null")
            .Map("databases/db/list/mongodb", "null")
            .Map("databases/db/list/redis", "null")
            .Map("databases/search", DatabasesJson)
            .Map("databases/pg/search", """{"total":0,"items":null}""")
            .Map("databases/mongodb/search", """{"total":0,"items":null}""");

    private static DatabaseService CreateService(
        IReadOnlyList<PanelFleet.Application.Models.NodeRecord> nodes,
        FakePanelClientFactory factory) =>
        new(
            new FakeNodeStore(nodes),
            new FakeSecretProtector(),
            factory,
            new DatabaseOptions { MaxConcurrency = 2, PerInstancePageSize = 50 },
            TimeProvider.System,
            NullLogger<DatabaseService>.Instance);
}
