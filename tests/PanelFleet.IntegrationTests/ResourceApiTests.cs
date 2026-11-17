using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;

namespace PanelFleet.IntegrationTests;

public sealed class ResourceApiTests
{
    private static readonly string[] Kinds = ["websites", "apps", "containers", "databases", "cronjobs"];

    [Fact]
    public async Task Resources_WithoutAuthentication_Returns401()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/resources/apps");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Resources_AllKinds_ReturnExpectedRows()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");

        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["websites"] = 2,
            ["apps"] = 2,
            ["containers"] = 2,
            ["databases"] = 2,
            ["cronjobs"] = 1
        };

        foreach (var kind in Kinds)
        {
            using var document = await GetResourcesAsync(client, kind);
            var rows = document.RootElement.GetProperty("rows");
            Assert.Equal(expected[kind], rows.GetArrayLength());
            Assert.Equal(1, document.RootElement.GetProperty("nodesSucceeded").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("nodesFailed").GetInt32());
        }
    }

    [Fact]
    public async Task Resources_PartialFailure_KeepsOtherNodes()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");
        await CreateNodeAsync(client, token, cookie, "bad-node");
        factory.Panels.SetBehavior("bad-node", FakePanelBehavior.Network);

        using var document = await GetResourcesAsync(client, "apps");

        Assert.Equal(2, document.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("nodesSucceeded").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("nodesFailed").GetInt32());

        var bad = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("nodeName").GetString() == "bad-node");
        Assert.Equal("Failed", bad.GetProperty("state").GetString());
        Assert.Equal("Network", bad.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Resources_Databases_UseInstancesAndRows()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");

        using var document = await GetResourcesAsync(client, "databases");
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal("MySQL", row.GetProperty("attributes").GetProperty("engine").GetString());
            Assert.Equal("MySQL", row.GetProperty("attributes").GetProperty("instance").GetString());
        });
        Assert.Equal(1, factory.Panels.CallsForPath("node-a", "databases/db/list/mysql"));
        Assert.Equal(1, factory.Panels.CallsForPath("node-a", "databases/search"));
    }

    [Fact]
    public async Task Resources_FilterAndPage_Work()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");
        await CreateNodeAsync(client, token, cookie, "node-b");

        using (var filtered = await GetResourcesAsync(client, "apps", "?filter=uptime&pageSize=10"))
        {
            Assert.Equal(2, filtered.RootElement.GetProperty("rows").GetArrayLength());
            Assert.All(
                filtered.RootElement.GetProperty("rows").EnumerateArray(),
                row => Assert.Contains("uptime", row.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase));
        }

        using var paged = await GetResourcesAsync(client, "apps", "?page=1&pageSize=1");
        Assert.Single(paged.RootElement.GetProperty("rows").EnumerateArray());
        Assert.True(paged.RootElement.GetProperty("totalPages").GetInt32() >= 4);
    }

    [Fact]
    public async Task Resources_Detail_ReturnsFields_And404ForUnknownNode()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");

        using var page = await GetResourcesAsync(client, "apps");
        var nodeId = page.RootElement.GetProperty("nodes").EnumerateArray().First().GetProperty("nodeId").GetString();

        var detailResponse = await client.GetAsync($"/api/resources/apps/{nodeId}/1");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(
            detail.RootElement.GetProperty("fields").GetProperty("name").GetString()));

        var missing = await client.GetAsync($"/api/resources/apps/{Guid.NewGuid():D}/1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var unknownKind = await client.GetAsync("/api/resources/unknown");
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
    }

    [Fact]
    public async Task Resources_RefreshEndpoint_RequiresCsrf()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var withoutCsrf = await client.PostAsJsonAsync("/api/resources/apps/refresh", new { });
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);

        var (token, cookie) = await GetCsrfAsync(client);
        var request = CreateJsonRequest(HttpMethod.Post, "/api/resources/apps/refresh", new { }, token, cookie);
        var accepted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task LegacyResourcesPage_RedirectsToAppStoreInstalledTab()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        // 旧资源清单页已与管理页 1:1 合并（docs/19 问题 9）：/resources/apps → /appstore?tab=installed。
        var response = await client.GetAsync("/resources/apps");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/appstore?tab=installed", response.Headers.Location?.OriginalString);
    }

    private static async Task<JsonDocument> GetResourcesAsync(HttpClient client, string kind, string query = "")
    {
        var response = await client.GetAsync($"/api/resources/{kind}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateNodeAsync(
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
        return document.RootElement.GetProperty("id").GetString()!;
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
