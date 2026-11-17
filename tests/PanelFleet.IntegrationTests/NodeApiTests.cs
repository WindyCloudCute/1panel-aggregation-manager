using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Infrastructure.Persistence;

namespace PanelFleet.IntegrationTests;

public sealed class NodeApiTests
{
    [Fact]
    public async Task Healthz_IsAnonymous()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoginPage_IsAnonymous()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Pocket ID", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nodes_WithoutAuthentication_Returns401()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/nodes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Nodes_WithAdmin_ReturnsEmptyList()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/api/nodes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task CreateNode_WithoutCsrf_IsRejected()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/nodes", new
        {
            name = "node-1",
            baseUrl = "https://panel.example.test",
            apiKey = "test-api-key-1234",
            authMode = "Auto",
            tlsPolicy = "Strict",
            isEnabled = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNode_HidesApiKeyAndStoresCiphertext()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        var response = await CreateNodeAsync(client, token, cookie);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("test-api-key-1234", body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("id", out _));
        Assert.Equal("1234", document.RootElement.GetProperty("apiKeyHint").GetString());

        var list = await client.GetStringAsync("/api/nodes");
        Assert.Contains("1234", list, StringComparison.Ordinal);
        Assert.Contains("myentrance", list, StringComparison.Ordinal);
        Assert.DoesNotContain("test-api-key-1234", list, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PanelFleetDbContext>();
        var entity = await dbContext.Nodes.SingleAsync();
        Assert.DoesNotContain("test-api-key-1234", entity.ApiKeyProtected, StringComparison.Ordinal);
        Assert.Equal("1234", entity.ApiKeyHint);
        Assert.Equal("myentrance", entity.SecurityEntrance);
    }

    [Fact]
    public async Task DuplicateNode_ReturnsConflict()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        var first = await CreateNodeAsync(client, token, cookie);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateNodeAsync(client, token, cookie);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task UpdateNode_WithoutApiKey_KeepsHintAndConnection()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        var created = await CreateNodeAsync(client, token, cookie);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdDocument.RootElement.GetProperty("id").GetString()!;

        var updateRequest = CreateJsonRequest(HttpMethod.Put, $"/api/nodes/{id}", new
        {
            name = "node-1-renamed",
            baseUrl = "https://panel.example.test",
            apiKey = (string?)null,
            authMode = "Auto",
            tlsPolicy = "Strict",
            tags = new[] { "prod" },
            remark = "updated",
            isEnabled = true
        }, token, cookie);

        var updated = await client.SendAsync(updateRequest);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var testRequest = CreateJsonRequest(HttpMethod.Post, $"/api/nodes/{id}/test", new { }, token, cookie);
        var tested = await client.SendAsync(testRequest);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);

        using var testedDocument = JsonDocument.Parse(await tested.Content.ReadAsStringAsync());
        Assert.True(testedDocument.RootElement.GetProperty("authorized").GetBoolean());

        var list = await client.GetStringAsync("/api/nodes");
        Assert.Contains("1234", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteNode_WritesAuditRecord()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        var created = await CreateNodeAsync(client, token, cookie);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdDocument.RootElement.GetProperty("id").GetString()!;

        var deleteRequest = CreateJsonRequest(HttpMethod.Delete, $"/api/nodes/{id}", null, token, cookie);
        var deleted = await client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var audit = await client.GetStringAsync("/api/audit");
        Assert.Contains("node.deleted", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestDraft_ReturnsProbeResult()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        var request = CreateJsonRequest(HttpMethod.Post, "/api/nodes/test", new
        {
            baseUrl = "https://panel.example.test",
            apiKey = "test-api-key-1234",
            authMode = "Auto",
            tlsPolicy = "Strict"
        }, token, cookie);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("authorized").GetBoolean());
        Assert.Equal("2.0.15", document.RootElement.GetProperty("panelVersion").GetString());
    }

    [Fact]
    public async Task DatabaseMigrations_AreApplied()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PanelFleetDbContext>();
        var applied = await dbContext.Database.GetAppliedMigrationsAsync();

        Assert.NotEmpty(applied);
    }

    [Fact]
    public async Task Readyz_ReturnsOk()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> CreateNodeAsync(
        HttpClient client,
        string token,
        string cookie)
    {
        var request = CreateJsonRequest(HttpMethod.Post, "/api/nodes", new
        {
            name = "node-1",
            baseUrl = "https://panel.example.test",
            apiKey = "test-api-key-1234",
            authMode = "Auto",
            tlsPolicy = "Strict",
            tags = new[] { "prod" },
            remark = "integration",
            isEnabled = true,
            securityEntrance = "myentrance"
        }, token, cookie);

        return await client.SendAsync(request);
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
}
