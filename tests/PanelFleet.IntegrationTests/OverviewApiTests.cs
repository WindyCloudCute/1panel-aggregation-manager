using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;

namespace PanelFleet.IntegrationTests;

public sealed class OverviewApiTests
{
    [Fact]
    public async Task Overview_WithoutAuthentication_Returns401()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Overview_EmptyDatabase_ReturnsEmptyCluster()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        using var document = await GetOverviewAsync(client);

        Assert.Equal(0, document.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("totals").GetProperty("nodeCount").GetInt32());
    }

    [Fact]
    public async Task HomePage_RendersForAdmin()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("总览", html, StringComparison.Ordinal);
        Assert.Contains("添加节点", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_CollectsMetrics_AndReportsPartialFailure()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        await CreateNodeAsync(client, token, cookie, "ok-node");
        await CreateNodeAsync(client, token, cookie, "down-node");
        factory.Panels.SetBehavior("down-node", FakePanelBehavior.Network);

        await RefreshAsync(factory);

        using var document = await GetOverviewAsync(client);
        var totals = document.RootElement.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("enabledCount").GetInt32());
        Assert.Equal(1, totals.GetProperty("nodesWithData").GetInt32());
        Assert.Equal(3, totals.GetProperty("websiteCount").GetInt32());
        Assert.Equal(1, totals.GetProperty("onlineCount").GetInt32());
        Assert.Equal(1, totals.GetProperty("offlineCount").GetInt32());

        var ok = FindNode(document, "ok-node");
        Assert.Equal("Fresh", ok.GetProperty("state").GetString());
        Assert.Equal("Online", ok.GetProperty("liveHealth").GetString());
        Assert.Equal(3, ok.GetProperty("metrics").GetProperty("websiteCount").GetInt32());
        Assert.Equal("/data/Routing", ok.GetProperty("metrics").GetProperty("maxDiskPath").GetString());
        var disks = ok.GetProperty("metrics").GetProperty("disks");
        Assert.Equal(2, disks.GetArrayLength());
        Assert.Contains(disks.EnumerateArray(), disk =>
            string.Equals(disk.GetProperty("path").GetString(), "/data/Routing", StringComparison.Ordinal));

        var down = FindNode(document, "down-node");
        Assert.Equal("Failed", down.GetProperty("state").GetString());
        Assert.Equal("Offline", down.GetProperty("liveHealth").GetString());
        Assert.Equal("Network", down.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TenNodes_PerformanceAndPartialFailure_WithinBudget()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        for (var index = 0; index < 10; index++)
        {
            await CreateNodeAsync(client, token, cookie, $"node-{index}");
        }

        factory.Panels.SetBehavior("node-8", FakePanelBehavior.Slow);
        factory.Panels.SetBehavior("node-9", FakePanelBehavior.Slow);
        factory.Panels.SetBehavior("node-7", FakePanelBehavior.Unauthorized);

        var stopwatch = Stopwatch.StartNew();
        await RefreshAsync(factory);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12), $"刷新耗时 {stopwatch.Elapsed} 超过 12 秒");

        using var document = await GetOverviewAsync(client);
        var totals = document.RootElement.GetProperty("totals");
        Assert.Equal(10, totals.GetProperty("enabledCount").GetInt32());
        Assert.Equal(7, totals.GetProperty("nodesWithData").GetInt32());
        Assert.Equal(21, totals.GetProperty("websiteCount").GetInt32());
        Assert.Equal(3, totals.GetProperty("offlineCount").GetInt32());

        Assert.Equal("Timeout", FindNode(document, "node-8").GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("Unauthorized", FindNode(document, "node-7").GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("Fresh", FindNode(document, "node-0").GetProperty("state").GetString());
        Assert.Equal(1, factory.Panels.Calls("node-0"));
        Assert.Equal(1, factory.Panels.Calls("node-8"));
    }

    [Fact]
    public async Task DisabledNode_IsNotCalled()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        await CreateNodeAsync(client, token, cookie, "disabled-node", enabled: false);

        await RefreshAsync(factory);

        Assert.Equal(0, factory.Panels.Calls("disabled-node"));

        using var document = await GetOverviewAsync(client);
        var node = FindNode(document, "disabled-node");
        Assert.Equal("Disabled", node.GetProperty("state").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("totals").GetProperty("disabledCount").GetInt32());
    }

    [Fact]
    public async Task Circuit_OpensAfterThreshold_AndSkipsNextRound()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        await CreateNodeAsync(client, token, cookie, "broken-node");
        factory.Panels.SetBehavior("broken-node", FakePanelBehavior.Network);

        for (var round = 0; round < 3; round++)
        {
            await RefreshAsync(factory);
        }

        Assert.Equal(3, factory.Panels.Calls("broken-node"));

        await RefreshAsync(factory);
        Assert.Equal(3, factory.Panels.Calls("broken-node"));

        using var document = await GetOverviewAsync(client);
        Assert.True(FindNode(document, "broken-node").GetProperty("circuitOpen").GetBoolean());
    }

    [Fact]
    public async Task RefreshEndpoint_RequiresCsrf_AndAccepts()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var withoutCsrf = await client.PostAsJsonAsync("/api/overview/refresh", new { });
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);

        var (token, cookie) = await GetCsrfAsync(client);
        var request = CreateJsonRequest(HttpMethod.Post, "/api/overview/refresh", new { }, token, cookie);
        var accepted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task SingleNodeRefresh_ReturnsSnapshot_And404ForUnknownNode()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);

        var nodeId = await CreateNodeAsync(client, token, cookie, "single-node");

        var request = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/overview/nodes/{nodeId}/refresh",
            new { },
            token,
            cookie);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Fresh", document.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, factory.Panels.Calls("single-node"));

        var missing = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/overview/nodes/{Guid.NewGuid():D}/refresh",
            new { },
            token,
            cookie);
        var missingResponse = await client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    private static async Task RefreshAsync(PanelFleetWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOverviewService>();
        await service.RefreshAllAsync(CancellationToken.None);
    }

    private static async Task<JsonDocument> GetOverviewAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement FindNode(JsonDocument document, string name) =>
        document.RootElement
            .GetProperty("nodes")
            .EnumerateArray()
            .First(node => string.Equals(node.GetProperty("name").GetString(), name, StringComparison.Ordinal));

    private static async Task<string> CreateNodeAsync(
        HttpClient client,
        string token,
        string cookie,
        string name,
        bool enabled = true)
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
                isEnabled = enabled,
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
