using System.Net;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// 旧资源清单页已与管理页 1:1 合并（docs/19 问题 9）：
/// `/resources/{kind}` 只保留为旧书签入口，由服务端 302 到对应管理页，并透传查询串。
/// </summary>
public sealed class ResourceTreeUiTests
{
    public static TheoryData<string, string> Redirects => new()
    {
        { "/resources/websites", "/websites" },
        { "/resources/apps", "/appstore?tab=installed" },
        { "/resources/containers", "/containers" },
        { "/resources/databases", "/databases" },
        { "/resources/cronjobs", "/cronjobs" }
    };

    [Theory]
    [MemberData(nameof(Redirects))]
    public async Task LegacyResourceRoute_RedirectsToManagementPage(string path, string expectedLocation)
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LegacyResourceRoute_KindIsCaseInsensitive()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/resources/Websites");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/websites", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(
        "/resources/websites?nodeId=6f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b",
        "/websites?nodeId=6f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b")]
    [InlineData(
        "/resources/cronjobs?id=42&nodeId=6f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b",
        "/cronjobs?id=42&nodeId=6f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b")]
    public async Task LegacyResourceRoute_PreservesQueryString(string path, string expectedLocation)
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LegacyResourceRoute_MergesQueryStringIntoAppsTabTarget()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/resources/apps?nodeId=6f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "/appstore?tab=installed&nodeId=6f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b",
            response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/resources/unknown")]
    [InlineData("/resources/resource-list")]
    public async Task LegacyResourceRoute_UnknownKind_ReturnsNotFound(string path)
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
