using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Settings;

namespace PanelFleet.IntegrationTests;

public sealed class SettingsApiTests
{
    [Fact]
    public async Task GetPanelFleetSettings_ReturnsValuesDefaultsAndEnvironment()
    {
        var settingsPath = CreateSettingsPath();
        using var factory = CreateFactory(settingsPath);
        using var client = factory.CreateAdminClient();

        using var response = await client.GetAsync("/api/settings/panelfleet");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("revision", out _));
        Assert.True(root.TryGetProperty("values", out var values));
        Assert.True(values.TryGetProperty("overviewRefreshIntervalSeconds", out _));
        Assert.True(values.TryGetProperty("resourcesPerNodeLimit", out _));
        Assert.True(root.TryGetProperty("defaults", out _));
        Assert.True(root.TryGetProperty("sources", out _));
        Assert.True(root.TryGetProperty("fields", out var fields));
        Assert.True(fields.GetArrayLength() >= 10);

        var environment = root.GetProperty("environment");
        Assert.False(string.IsNullOrWhiteSpace(environment.GetProperty("dataDirectory").GetString()));
        Assert.True(environment.TryGetProperty("nodeCount", out _));
        Assert.True(environment.TryGetProperty("version", out _));
    }

    [Fact]
    public async Task PutPanelFleetSettings_WithoutCsrfToken_IsRejected()
    {
        var settingsPath = CreateSettingsPath();
        using var factory = CreateFactory(settingsPath);
        using var client = factory.CreateAdminClient();

        using var response = await client.PutAsJsonAsync(
            "/api/settings/panelfleet",
            new { resourcesPageSize = 120 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutPanelFleetSettings_WithCsrfToken_AppliesAndAudits()
    {
        var settingsPath = CreateSettingsPath();
        using var factory = CreateFactory(settingsPath);
        using var client = factory.CreateAdminClient();

        var token = await GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings/panelfleet")
        {
            Content = JsonContent.Create(new { resourcesPageSize = 120, terminalEnabled = false })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var changedKeys = document.RootElement.GetProperty("changedKeys")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(key => key is not null)
            .ToArray();
        Assert.Contains("resourcesPageSize", changedKeys);
        Assert.Contains("terminalEnabled", changedKeys);

        using var auditResponse = await client.GetAsync("/api/audit?action=settings.updated");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var auditJson = await auditResponse.Content.ReadAsStringAsync();
        Assert.Contains("settings.updated", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("oldPassword", auditJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutPanelFleetSettings_InvalidValue_ReturnsValidationProblem()
    {
        var settingsPath = CreateSettingsPath();
        using var factory = CreateFactory(settingsPath);
        using var client = factory.CreateAdminClient();

        var token = await GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings/panelfleet")
        {
            Content = JsonContent.Create(new { resourcesPageSize = 1 })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetPanelSettings_UnknownNode_ReturnsNotFound()
    {
        var settingsPath = CreateSettingsPath();
        using var factory = CreateFactory(settingsPath);
        using var client = factory.CreateAdminClient();

        using var response = await client.GetAsync($"/api/settings/panel/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPanelFleetSettings_Anonymous_IsUnauthorized()
    {
        var settingsPath = CreateSettingsPath();
        using var factory = CreateFactory(settingsPath);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/settings/panelfleet");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 测试基建的 <c>TestAuthHandler</c> 会给所有 X-Test-User 都注入 admin 角色，
    /// 因此无法用假登录用户验证 403；这里改为断言端点元数据确实要求 Admin 策略。
    /// </summary>
    [Fact]
    public void SettingsEndpoints_RequireAdminPolicy()
    {
        var settingsPath = CreateSettingsPath();
        using var factory = CreateFactory(settingsPath);

        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.RoutePattern.RawText?.TrimStart('/'),
                "api/settings/panelfleet",
                StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(endpoint);
        var policies = endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .ToArray();
        Assert.Contains(policies, policy => policy!.Contains("Admin", StringComparison.Ordinal));
    }

    private static PanelFleetWebApplicationFactory CreateFactory(string settingsPath)
    {
        var factory = new PanelFleetWebApplicationFactory
        {
            AdditionalServices = services =>
            {
                services.RemoveAll<SettingsModuleOptions>();
                services.AddSingleton(new SettingsModuleOptions { FilePath = settingsPath });
            }
        };

        return factory;
    }

    private static string CreateSettingsPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"panelfleet-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/antiforgery/token");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("未取得 CSRF 令牌。");
    }
}
