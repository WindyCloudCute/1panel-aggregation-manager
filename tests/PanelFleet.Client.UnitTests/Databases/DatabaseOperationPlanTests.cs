using System.Text.Json.Nodes;
using PanelFleet.Application.Databases;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.UnitTests.Containers;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Databases;

public sealed class DatabaseOperationPlanTests
{
    [Fact]
    public async Task CreateMySql_IsMedium_WithInstanceAndDatabaseSummary()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new DatabaseCreateHandler();
        var parameters = CreateParameters("mysql");
        var context = PlanContext(handler.Kind, [node], parameters);

        var plan = await handler.PlanAsync(context, CancellationToken.None);

        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Null(plan.TypedConfirmationText);
        Assert.Equal("database", plan.TargetType);
        Assert.Single(plan.Targets);
        Assert.Contains("MySQL", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("app_data", plan.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app data")]
    [InlineData("app;drop")]
    [InlineData("app/../../x")]
    public async Task CreateMySql_InvalidDatabaseName_IsRejected(string name)
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = CreateParameters("mysql");
        parameters["name"] = name;
        var handler = new DatabaseCreateHandler();

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));
    }

    [Fact]
    public async Task CreatePostgreSql_RequiresUserName()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = CreateParameters("postgresql");
        parameters.Remove("username");
        var handler = new DatabaseCreateHandler();

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));

        Assert.Contains("用户名", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateMongo_InvalidPermission_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = CreateParameters("mongodb");
        parameters["permission"] = "root";
        var handler = new DatabaseCreateHandler();

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_IsCritical_WithDatabaseNameAsTypedConfirmation()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new DatabaseDeleteHandler();
        var parameters = new JsonObject
        {
            ["engine"] = "mysql",
            ["instance"] = "MySQL",
            ["from"] = "remote",
            ["id"] = 3L,
            ["database"] = "app_data"
        };

        var plan = await handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
        Assert.Equal("app_data", plan.TypedConfirmationText);
        Assert.Contains(plan.Warnings, warning => warning.Contains("永久删除", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_WithoutId_IsRejected()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new DatabaseDeleteHandler();
        var parameters = new JsonObject
        {
            ["engine"] = "mysql",
            ["instance"] = "MySQL",
            ["database"] = "app_data"
        };

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None));

        Assert.Contains("id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescriptionUpdate_IsMedium()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new DatabaseDescriptionUpdateHandler();
        var parameters = new JsonObject
        {
            ["engine"] = "mysql",
            ["instance"] = "MySQL",
            ["id"] = 1L,
            ["description"] = "新的描述"
        };

        var plan = await handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None);

        Assert.Equal(OperationRisk.Medium, plan.Risk);
    }

    [Fact]
    public async Task UserOperations_AreHigh_WithUserNameAsTypedConfirmation()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["instance"] = "MySQL",
            ["username"] = "app_user",
            ["host"] = "%",
            ["password"] = "s3cret"
        };

        var create = await new DatabaseUserCreateHandler()
            .PlanAsync(PlanContext("database.user_create", [node], parameters), CancellationToken.None);
        var password = await new DatabaseUserPasswordHandler()
            .PlanAsync(PlanContext("database.user_password", [node], parameters), CancellationToken.None);
        var delete = await new DatabaseUserDeleteHandler()
            .PlanAsync(PlanContext("database.user_delete", [node], parameters), CancellationToken.None);

        Assert.Equal(OperationRisk.High, create.Risk);
        Assert.Equal("app_user", create.TypedConfirmationText);
        Assert.Equal(OperationRisk.High, password.Risk);
        Assert.Equal("app_user", password.TypedConfirmationText);
        Assert.Equal(OperationRisk.High, delete.Risk);
        Assert.Equal("app_user", delete.TypedConfirmationText);
    }

    [Fact]
    public async Task GrantAndRevoke_AreHigh_WithDatabaseNameAsTypedConfirmation()
    {
        var node = ContainerTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["instance"] = "MySQL",
            ["db"] = "app_data",
            ["username"] = "app_user",
            ["host"] = "%"
        };

        var grant = await new DatabaseGrantHandler()
            .PlanAsync(PlanContext("database.grant", [node], parameters), CancellationToken.None);
        var revoke = await new DatabaseGrantRevokeHandler()
            .PlanAsync(PlanContext("database.grant_revoke", [node], parameters), CancellationToken.None);

        Assert.Equal(OperationRisk.High, grant.Risk);
        Assert.Equal("app_data", grant.TypedConfirmationText);
        Assert.Equal(OperationRisk.High, revoke.Risk);
        Assert.Equal("app_data", revoke.TypedConfirmationText);
    }

    [Fact]
    public async Task RedisConfig_IsHigh_AndValidatesNumbers()
    {
        var node = ContainerTestData.Node("node-a");
        var handler = new RedisConfigUpdateHandler();
        var parameters = new JsonObject
        {
            ["instance"] = "redis",
            ["timeout"] = "300",
            ["maxclients"] = "10000"
        };

        var plan = await handler.PlanAsync(PlanContext(handler.Kind, [node], parameters), CancellationToken.None);
        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Equal("redis", plan.TypedConfirmationText);

        var invalid = new JsonObject { ["instance"] = "redis", ["timeout"] = "abc" };
        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, [node], invalid), CancellationToken.None));
    }

    [Fact]
    public async Task AllDatabaseOperations_RejectMultiNode()
    {
        var nodes = new[] { ContainerTestData.Node("node-a"), ContainerTestData.Node("node-b") };
        var handler = new DatabaseCreateHandler();

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.PlanAsync(PlanContext(handler.Kind, nodes, CreateParameters("mysql")), CancellationToken.None));

        Assert.Contains("单节点", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCreateMySql_Base64EncodesPassword_AndCallsMySqlRoute()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory().Map("api/v2/databases", "{}");
        var handler = new DatabaseCreateHandler();
        using var client = factory.Create(CreateDescriptor(node));

        var step = await handler.ExecuteAsync(
            new OperationNodeContext(
                Guid.NewGuid(),
                node,
                client,
                OperationParameters.From(CreateParameters("mysql"))),
            CancellationToken.None);

        Assert.True(step.Success);
        var call = Assert.Single(factory.Calls);
        Assert.Equal("api/v2/databases", call.Path);
        Assert.Contains("\"password\":\"czNjcmV0\"", call.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"database\":\"MySQL\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"permission\":\"%\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteDeletePostgreSql_UsesPgRoute()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory().Map("databases/pg/del", "{}");
        var handler = new DatabaseDeleteHandler();
        using var client = factory.Create(CreateDescriptor(node));
        var parameters = new JsonObject
        {
            ["engine"] = "postgresql",
            ["instance"] = "PostgreSQL",
            ["from"] = "remote",
            ["id"] = 9L,
            ["database"] = "app_data",
            ["forceDelete"] = true,
            ["deleteBackup"] = false
        };

        var step = await handler.ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        var call = Assert.Single(factory.Calls);
        Assert.Contains("databases/pg/del", call.Path, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"postgresql\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"forceDelete\":true", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteUserPassword_Base64EncodesPassword()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory().Map("databases/users/password", "{}");
        var handler = new DatabaseUserPasswordHandler();
        using var client = factory.Create(CreateDescriptor(node));
        var parameters = new JsonObject
        {
            ["instance"] = "MySQL",
            ["username"] = "app_user",
            ["host"] = "%",
            ["password"] = "s3cret"
        };

        var step = await handler.ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        var call = Assert.Single(factory.Calls);
        Assert.Contains("\"password\":\"czNjcmV0\"", call.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRedisConfig_CallsRedisConfUpdateRoute()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new FakePanelClientFactory().Map("databases/redis/conf/update", "{}");
        var handler = new RedisConfigUpdateHandler();
        using var client = factory.Create(CreateDescriptor(node));
        var parameters = new JsonObject
        {
            ["instance"] = "redis",
            ["dbType"] = "redis",
            ["timeout"] = "300"
        };

        var step = await handler.ExecuteAsync(
            new OperationNodeContext(Guid.NewGuid(), node, client, OperationParameters.From(parameters)),
            CancellationToken.None);

        Assert.True(step.Success);
        var call = Assert.Single(factory.Calls);
        Assert.Contains("databases/redis/conf/update", call.Path, StringComparison.Ordinal);
        Assert.Contains("\"timeout\":\"300\"", call.Body, StringComparison.Ordinal);
    }

    private static JsonObject CreateParameters(string engine) => new()
    {
        ["engine"] = engine,
        ["instance"] = engine switch
        {
            "postgresql" => "PostgreSQL",
            "mongodb" => "MongoDB",
            _ => "MySQL"
        },
        ["from"] = "remote",
        ["name"] = "app_data",
        ["format"] = "utf8mb4",
        ["collation"] = "utf8mb4_general_ci",
        ["username"] = "app_user",
        ["password"] = "s3cret",
        ["permission"] = "%",
        ["description"] = "业务库"
    };

    private static OperationPlanContext PlanContext(
        string kind,
        IReadOnlyList<NodeRecord> nodes,
        JsonObject parameters)
    {
        var invocation = new OperationInvocation(kind, nodes.Select(node => node.Id).ToArray(), parameters);
        return new OperationPlanContext(invocation, nodes, OperationParameters.From(parameters));
    }

    private static NodeDescriptor CreateDescriptor(NodeRecord node) => new(
        node.Id,
        node.Name,
        node.BaseUrl,
        "api-key",
        AuthMode.HmacSha256,
        TlsPolicy.Strict);
}


