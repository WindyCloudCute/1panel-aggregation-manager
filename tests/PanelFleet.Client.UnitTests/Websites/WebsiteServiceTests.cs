using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application;
using PanelFleet.Application.Websites;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Websites;

/// <summary>网站管理只读服务单元测试（假 IPanelClientFactory + 假 INodeStore，不访问网络）。</summary>
public sealed class WebsiteServiceTests
{
    [Fact]
    public async Task ListAsync_MergesGroups_AndMapsRows()
    {
        var factory = CreateListFactory();
        var (service, node) = CreateService(factory);

        var result = await service.ListAsync(new WebsiteQuery(node.Id.Value, null, null, 1, 20), CancellationToken.None);

        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(2, result.Groups.Count);
        Assert.Empty(result.Warnings);

        // 收藏优先排序：site-a favorite=true 排在前面。
        Assert.Equal("site-a.example.com", result.Rows[0].PrimaryDomain);
        Assert.True(result.Rows[0].Favorite);
        Assert.Equal("默认分组", result.Rows[0].GroupName);
        Assert.Equal("项目A", result.Rows[1].GroupName);
        Assert.Equal("site-b.example.com", result.Rows[1].PrimaryDomain);

        var calls = factory.Calls.Where(call => call.Path.Contains("websites/search", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains("websiteGroupId", calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"created_at\"", calls[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_GroupFilter_ReturnsOnlyThatGroup()
    {
        var factory = CreateListFactory();
        var (service, node) = CreateService(factory);

        var result = await service.ListAsync(new WebsiteQuery(node.Id.Value, 2, null, 1, 20), CancellationToken.None);

        var row = Assert.Single(result.Rows);
        Assert.Equal("site-b.example.com", row.PrimaryDomain);
        Assert.Equal("项目A", row.GroupName);
        Assert.Equal(2, result.Groups.Count);
    }

    [Fact]
    public async Task ListAsync_NameFilter_AndPagination()
    {
        var factory = CreateListFactory();
        var (service, node) = CreateService(factory);

        var filtered = await service.ListAsync(new WebsiteQuery(node.Id.Value, null, "site-b", 1, 20), CancellationToken.None);
        Assert.Single(filtered.Rows);
        Assert.Equal("site-b.example.com", filtered.Rows[0].PrimaryDomain);

        var paged = await service.ListAsync(new WebsiteQuery(node.Id.Value, null, null, 1, 1), CancellationToken.None);
        Assert.Single(paged.Rows);
        Assert.Equal(2, paged.TotalMatched);
        Assert.Equal(2, paged.TotalPages);
    }

    [Fact]
    public async Task ListAsync_NoGroups_FallsBackToUngrouped()
    {
        var factory = new RecordingWebsiteClientFactory()
            .Respond("groups/search", "[]")
            .Respond("websites/search", WebsiteTestJsonData.GroupOneWebsite);
        var (service, node) = CreateService(factory);

        var result = await service.ListAsync(new WebsiteQuery(node.Id.Value, null, null, 1, 20), CancellationToken.None);

        var row = Assert.Single(result.Rows);
        Assert.Equal("未分组", row.GroupName);
        Assert.Equal(0, row.GroupId);
    }

    [Fact]
    public async Task ListAsync_GroupsError_AddsWarning_AndFallsBack()
    {
        var factory = new RecordingWebsiteClientFactory()
            .Fail("groups/search", PanelErrorKind.Network)
            .Respond("websites/search", WebsiteTestJsonData.GroupOneWebsite);
        var (service, node) = CreateService(factory);

        var result = await service.ListAsync(new WebsiteQuery(node.Id.Value, null, null, 1, 20), CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Contains(result.Warnings, warning => warning.Contains("无法读取网站分组", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDetailAsync_MapsFields_AndDomains()
    {
        var factory = new RecordingWebsiteClientFactory()
            .Respond("groups/search", WebsiteTestJsonData.Groups)
            .Respond("websites/11", WebsiteTestJsonData.Detail);
        var (service, node) = CreateService(factory);

        var detail = await service.GetDetailAsync(node.Id.Value, 11, CancellationToken.None);

        Assert.Equal(11, detail.Id);
        Assert.Equal("site-a.example.com", detail.PrimaryDomain);
        Assert.Equal("PHP-8030", detail.RuntimeName);
        Assert.Equal(2, detail.GroupId);
        Assert.Equal("项目A", detail.GroupName);
        Assert.True(detail.Favorite);
        Assert.True(detail.IPv6);
        Assert.True(detail.AccessLogEnabled);
        Assert.False(detail.ErrorLogEnabled);
        Assert.Equal(2, detail.Domains.Count);
        Assert.Contains("www.site-a.example.com", detail.Domains);
        Assert.Contains("error.log", detail.ErrorLogPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDomainsAsync_MapsDomains()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("websites/domains/11", WebsiteTestJsonData.Domains);
        var (service, node) = CreateService(factory);

        var domains = await service.GetDomainsAsync(node.Id.Value, 11, CancellationToken.None);

        Assert.Equal(2, domains.Count);
        Assert.Equal("site-a.example.com", domains[0].Domain);
        Assert.True(domains[0].Ssl);
        Assert.Equal(81, domains[1].Port);
    }

    [Fact]
    public async Task GetProxiesAsync_MapsProxyConfig()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("websites/proxies", WebsiteTestJsonData.Proxies);
        var (service, node) = CreateService(factory);

        var proxies = await service.GetProxiesAsync(node.Id.Value, 11, CancellationToken.None);

        var proxy = Assert.Single(proxies);
        Assert.Equal("api", proxy.Name);
        Assert.Equal("/api", proxy.Match);
        Assert.Equal("http://127.0.0.1:8080", proxy.ProxyPass);
        Assert.True(proxy.Enable);
        Assert.Equal(10, proxy.ServerCacheTime);
        Assert.Contains("\"id\":11", factory.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHttpsAsync_MapsCertificateSafely()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("websites/11/https", WebsiteTestJsonData.Https);
        var (service, node) = CreateService(factory);

        var https = await service.GetHttpsAsync(node.Id.Value, 11, CancellationToken.None);

        Assert.True(https.Enable);
        Assert.Equal("HTTPToHTTPS", https.HttpConfig);
        Assert.NotNull(https.Certificate);
        Assert.Equal("*.example.com", https.Certificate!.PrimaryDomain);
        Assert.Equal(2, https.SslProtocols.Count);
        Assert.Equal("443", https.HttpsPort);

        // 安全约束：DTO 模型不包含私钥/PEM，序列化后也不得出现。
        var json = JsonSerializer.Serialize(https);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN CERTIFICATE", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSslCertificatesAsync_ReturnsSafeMetadataOnly()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("websites/ssl/list", WebsiteTestJsonData.SslList);
        var (service, node) = CreateService(factory);

        var certificates = await service.GetSslCertificatesAsync(node.Id.Value, CancellationToken.None);

        var certificate = Assert.Single(certificates);
        Assert.Equal(2, certificate.Id);
        Assert.Equal("*.example.com", certificate.PrimaryDomain);
        Assert.Equal("ready", certificate.Status);
        Assert.EndsWith("cert.pem", certificate.CertPath, StringComparison.Ordinal);

        var json = JsonSerializer.Serialize(certificates);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN CERTIFICATE", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPhpRuntimesAsync_MapsVersionFromImage()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("runtimes/search", WebsiteTestJsonData.Runtimes);
        var (service, node) = CreateService(factory);

        var runtimes = await service.GetPhpRuntimesAsync(node.Id.Value, CancellationToken.None);

        var runtime = Assert.Single(runtimes);
        Assert.Equal(2, runtime.Id);
        Assert.Equal("PHP-8030", runtime.Name);
        Assert.Equal("8.0.30", runtime.Version);
        Assert.Equal("Running", runtime.Status);
        Assert.Equal("php", runtime.Type);
    }

    [Fact]
    public async Task MissingNode_ThrowsNodeNotFound()
    {
        var (service, _) = CreateService(new RecordingWebsiteClientFactory());

        await Assert.ThrowsAsync<NodeNotFoundException>(
            () => service.ListAsync(new WebsiteQuery(Guid.NewGuid(), null, null, 1, 20), CancellationToken.None));
    }

    [Fact]
    public async Task DisabledNode_ThrowsWebsiteException()
    {
        var node = WebsiteTestData.Node("disabled", enabled: false);
        var service = new WebsiteService(
            new WebsiteFakeNodeStore(node),
            new WebsiteFakeProtector(),
            new RecordingWebsiteClientFactory(),
            NullLogger<WebsiteService>.Instance);

        var exception = await Assert.ThrowsAsync<WebsiteException>(
            () => service.GetGroupsAsync(node.Id.Value, CancellationToken.None));
        Assert.Contains("已禁用", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PanelError_ThrowsWebsiteExceptionWithKind()
    {
        var factory = new RecordingWebsiteClientFactory().Fail("websites/search", PanelErrorKind.Unauthorized);
        var (service, node) = CreateService(factory);

        var exception = await Assert.ThrowsAsync<WebsiteException>(
            () => service.ListAsync(new WebsiteQuery(node.Id.Value, null, null, 1, 20), CancellationToken.None));

        Assert.Equal(PanelErrorKind.Unauthorized, exception.Error.Kind);
    }

    private static RecordingWebsiteClientFactory CreateListFactory()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("groups/search", WebsiteTestJsonData.Groups);
        factory.Responder = (path, body) =>
        {
            if (!path.Contains("websites/search", StringComparison.Ordinal) || body is null)
            {
                return null;
            }

            if (body.Contains("\"websiteGroupId\":2", StringComparison.Ordinal))
            {
                return WebsiteTestJsonData.GroupTwoWebsite;
            }

            if (body.Contains("\"websiteGroupId\":1", StringComparison.Ordinal))
            {
                return WebsiteTestJsonData.GroupOneWebsite;
            }

            return null;
        };

        return factory;
    }

    private static (WebsiteService Service, Application.Models.NodeRecord Node) CreateService(
        RecordingWebsiteClientFactory factory)
    {
        var node = WebsiteTestData.Node();
        var service = new WebsiteService(
            new WebsiteFakeNodeStore(node),
            new WebsiteFakeProtector(),
            factory,
            NullLogger<WebsiteService>.Instance);
        return (service, node);
    }
}
