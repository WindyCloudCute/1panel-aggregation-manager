using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Websites;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Client.Dto.Websites;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P7 网站管理契约测试：真实 <see cref="PanelClientFactory"/> 打到进程内假 1Panel，
/// 验证路由、请求体、DTO 映射（含 SSL 私钥不落 DTO）与业务错误映射。
/// </summary>
public sealed class WebsiteContractTests
{
    private const string GroupsJson = """
        [{"id":1,"name":"Default","type":"website","isDefault":true},{"id":2,"name":"项目A","type":"website","isDefault":false}]
        """;

    private const string DomainsJson = """
        [{"id":21,"websiteId":11,"domain":"site-a.example.com","port":80,"ssl":true},{"id":22,"websiteId":11,"domain":"www.site-a.example.com","port":81,"ssl":false}]
        """;

    private const string ProxiesJson = """
        [{"id":11,"enable":true,"cache":false,"cacheTime":0,"cacheUnit":"","serverCacheTime":10,"serverCacheUnit":"m","name":"api","match":"/api","proxyPass":"http://127.0.0.1:8080","proxyHost":"$host","sni":false,"proxySSLName":"","sslVerify":false,"cors":false,"allowOrigins":"","allowMethods":"","allowHeaders":"","allowCredentials":false,"preflight":false,"filePath":"/1panel/proxy/api.conf"}]
        """;

    private const string HttpsJson = """
        {"enable":true,"httpConfig":"HTTPToHTTPS","SSL":{"id":2,"primaryDomain":"*.example.com","domains":"*.example.com","type":"ZeroSSL RSA DV SSL CA 2","provider":"manual","status":"ready","expireDate":"2026-12-26T23:59:59Z","startDate":"2026-09-27T00:00:00Z","isIP":false,"certPath":"","privateKey":"-----BEGIN PRIVATE KEY-----secret","pem":"-----BEGIN CERTIFICATE-----secret"},"SSLProtocol":["TLSv1.2","TLSv1.3"],"algorithm":"","hsts":false,"hstsIncludeSubDomains":false,"httpsPort":"443","httpsPorts":[443],"http3":false}
        """;

    private const string SslListJson = """
        [{"id":2,"primaryDomain":"*.example.com","domains":"*.example.com","type":"ZeroSSL RSA DV SSL CA 2","provider":"manual","status":"ready","expireDate":"2026-12-26T23:59:59Z","autoRenew":false,"isIP":false,"keyType":"","privateKeyPath":"/1panel/ssl/key.pem","certPath":"/1panel/ssl/cert.pem","pushDir":false,"dir":"","privateKey":"-----BEGIN PRIVATE KEY-----secret","pem":"-----BEGIN CERTIFICATE-----secret"}]
        """;

    private const string RuntimesJson = """
        {"total":1,"items":[{"id":2,"name":"PHP-8030","resource":"appstore","appDetailID":544,"appID":219,"status":"Running","type":"php","image":"1panel-php-fpm:8.0.30","message":""}]}
        """;

    [Fact]
    public async Task Search_UsesRequiredParameters_AndMapsFields()
    {
        await using var server = new FakePanelServer(new FakePanelOptions());
        await server.StartAsync();
        using var client = CreateClient(server);

        // FakePanelServer 内置 POST /websites/search（与 P4 共享，校验 orderBy/order 必填）。
        var result = await client.PostAsync<PagedItemsDto<WebsiteListItemDto>>(
            WebsiteRoutes.Search,
            new WebsiteSearchRequest(1, 20, null, "created_at", "descending", 1, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data?.Total);
        var site = result.Data!.Items![0];
        Assert.Equal(1, site.Id);
        Assert.Equal("example.com", site.PrimaryDomain);
        Assert.Equal("static", site.Type);
        Assert.Equal("Running", site.Status);

        var body = server.GetLastRequestBody(WebsiteRoutes.Search);
        Assert.NotNull(body);
        Assert.Contains("\"orderBy\":\"created_at\"", body, StringComparison.Ordinal);
        Assert.Contains("\"websiteGroupId\":1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_AreMapped()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?> { [WebsiteRoutes.GroupsSearch] = GroupsJson }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var groups = await client.PostAsync<List<WebsiteGroupDto>>(
            WebsiteRoutes.GroupsSearch,
            new WebsiteGroupSearchRequest("website"));

        Assert.True(groups.IsSuccess);
        Assert.Equal(2, groups.Data!.Count);
        Assert.True(groups.Data[0].IsDefault);
        Assert.Equal("项目A", groups.Data[1].Name);
        Assert.Contains("\"type\":\"website\"", server.GetLastRequestBody(WebsiteRoutes.GroupsSearch), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_IsMapped_WithDomains()
    {
        await using var server = new FakePanelServer(new FakePanelOptions());
        await server.StartAsync();
        using var client = CreateClient(server);

        var detail = await client.GetAsync<WebsiteDetailDto>(WebsiteRoutes.Detail(1));

        Assert.True(detail.IsSuccess);
        Assert.Equal(1, detail.Data?.Id);
        Assert.Equal("example.com", detail.Data?.PrimaryDomain);
        Assert.NotNull(detail.Data!.Domains);
        Assert.Equal(JsonValueKind.Array, detail.Data.Domains!.Value.ValueKind);
        Assert.Equal(2, detail.Data.Domains.Value.GetArrayLength());
    }

    [Fact]
    public async Task DomainsProxiesHttpsSslRuntimes_AreMapped_WithoutPrivateKeys()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [WebsiteRoutes.Domains(11)] = DomainsJson,
                [WebsiteRoutes.Proxies] = ProxiesJson,
                [WebsiteRoutes.Https(11)] = HttpsJson,
                [WebsiteRoutes.SslList] = SslListJson,
                [WebsiteRoutes.RuntimesSearch] = RuntimesJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var domains = await client.GetAsync<List<WebsiteDomainDto>>(WebsiteRoutes.Domains(11));
        Assert.True(domains.IsSuccess);
        Assert.Equal(2, domains.Data!.Count);
        Assert.True(domains.Data[0].SSL);

        var proxies = await client.PostAsync<List<WebsiteProxyDto>>(WebsiteRoutes.Proxies, new { id = 11 });
        Assert.True(proxies.IsSuccess);
        Assert.Equal("/api", proxies.Data![0].Match);

        var https = await client.GetAsync<WebsiteHttpsDto>(WebsiteRoutes.Https(11));
        Assert.True(https.IsSuccess);
        Assert.True(https.Data!.Enable);
        Assert.Equal(443, https.Data.HttpsPorts![0]);
        Assert.NotNull(https.Data.SSL);
        Assert.Equal("*.example.com", https.Data.SSL!.PrimaryDomain);

        var ssl = await client.PostAsync<List<WebsiteSslDto>>(WebsiteRoutes.SslList, new { });
        Assert.True(ssl.IsSuccess);
        var certificate = Assert.Single(ssl.Data!);
        Assert.Equal("ready", certificate.Status);
        var sslJson = JsonSerializer.Serialize(certificate);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", sslJson, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN CERTIFICATE", sslJson, StringComparison.Ordinal);

        var runtimes = await client.PostAsync<PagedItemsDto<WebsiteRuntimeDto>>(
            WebsiteRoutes.RuntimesSearch,
            new WebsiteRuntimeSearchRequest(1, 200, "php"));
        Assert.True(runtimes.IsSuccess);
        Assert.Equal("PHP-8030", runtimes.Data!.Items![0].Name);

        Assert.Contains("\"id\":11", server.GetLastRequestBody(WebsiteRoutes.Proxies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRoutes_AcceptBodies()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [WebsiteRoutes.Operate] = """{}""",
                [WebsiteRoutes.Update] = """{}""",
                [WebsiteRoutes.DomainCreate] = """{}""",
                [WebsiteRoutes.DomainUpdate] = """{}""",
                [WebsiteRoutes.DomainDelete] = """{}""",
                [WebsiteRoutes.ProxyUpdate] = """{}""",
                [WebsiteRoutes.ProxyDelete] = """{}""",
                [WebsiteRoutes.ProxyStatus] = """{}""",
                [WebsiteRoutes.Https(11)] = """{}""",
                [WebsiteRoutes.SslObtain] = """{}""",
                ["api/v2/websites/php/version"] = """{}"""
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var start = await client.PostAsync<JsonElement>(WebsiteRoutes.Operate, new { id = 11, operate = "start" });
        Assert.True(start.IsSuccess);
        Assert.Contains("\"operate\":\"start\"", server.GetLastRequestBody(WebsiteRoutes.Operate), StringComparison.Ordinal);

        var update = await client.PostAsync<JsonElement>(
            WebsiteRoutes.Update,
            new { id = 11, primaryDomain = "new.example.com", favorite = true });
        Assert.True(update.IsSuccess);
        Assert.Contains("\"primaryDomain\":\"new.example.com\"", server.GetLastRequestBody(WebsiteRoutes.Update), StringComparison.Ordinal);

        var domainCreate = await client.PostAsync<JsonElement>(
            WebsiteRoutes.DomainCreate,
            new { websiteID = 11, domains = new[] { new { domain = "new.example.com", port = 80, ssl = false } } });
        Assert.True(domainCreate.IsSuccess);
        Assert.Contains("\"websiteID\":11", server.GetLastRequestBody(WebsiteRoutes.DomainCreate), StringComparison.Ordinal);

        var domainDelete = await client.PostAsync<JsonElement>(WebsiteRoutes.DomainDelete, new { id = 21 });
        Assert.True(domainDelete.IsSuccess);

        var proxy = await client.PostAsync<JsonElement>(
            WebsiteRoutes.ProxyUpdate,
            new { id = 11, operate = "create", name = "api", match = "/api", proxyPass = "http://127.0.0.1:8080", proxyHost = "$host" });
        Assert.True(proxy.IsSuccess);
        Assert.Contains("\"operate\":\"create\"", server.GetLastRequestBody(WebsiteRoutes.ProxyUpdate), StringComparison.Ordinal);

        var proxyStatus = await client.PostAsync<JsonElement>(
            WebsiteRoutes.ProxyStatus,
            new { id = 11, name = "api", status = "enable" });
        Assert.True(proxyStatus.IsSuccess);

        var https = await client.PostAsync<JsonElement>(
            WebsiteRoutes.Https(11),
            new { websiteId = 11, enable = true, websiteSSLID = 2, type = "existed", httpConfig = "HTTPToHTTPS" });
        Assert.True(https.IsSuccess);
        Assert.Contains("\"websiteSSLID\":2", server.GetLastRequestBody(WebsiteRoutes.Https(11)), StringComparison.Ordinal);

        var ssl = await client.PostAsync<JsonElement>(WebsiteRoutes.SslObtain, new { ID = 2 });
        Assert.True(ssl.IsSuccess);

        var php = await client.PostAsync<JsonElement>(
            "api/v2/websites/php/version",
            new { websiteID = 11, runtimeID = 2 });
        Assert.True(php.IsSuccess);
    }

    [Fact]
    public async Task CreateHelpers_AreMapped()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [WebsiteRoutes.CreateOptions] = """[{"id":1,"primaryDomain":"a.example.com","alias":"A"},{"id":2,"primaryDomain":"b.example.com"}]""",
                [WebsiteRoutes.CreateCheck] = """[{"name":"docker","status":"failed","version":"1.0","appName":"Docker"}]""",
                [WebsiteRoutes.AcmeSearch] = """{"total":1,"items":[{"id":5,"email":"ops@example.com","type":"letsencrypt","status":"valid","isDefault":true}]}"""
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var options = await client.PostAsync<List<WebsiteOptionDto>>(
            WebsiteRoutes.CreateOptions,
            new { types = new[] { "static", "runtime" } });
        Assert.True(options.IsSuccess);
        Assert.Equal(2, options.Data!.Count);
        Assert.Equal("a.example.com", options.Data[0].PrimaryDomain);
        Assert.Contains("\"types\":[\"static\",\"runtime\"]", server.GetLastRequestBody(WebsiteRoutes.CreateOptions), StringComparison.Ordinal);

        var issues = await client.PostAsync<List<WebsitePrecheckItemDto>>(
            WebsiteRoutes.CreateCheck,
            new { installIds = Array.Empty<long>() });
        Assert.True(issues.IsSuccess);
        var issue = Assert.Single(issues.Data!);
        Assert.Equal("docker", issue.Name);
        Assert.Equal("failed", issue.Status);

        var accounts = await client.PostAsync<PagedItemsDto<WebsiteAcmeAccountDto>>(
            WebsiteRoutes.AcmeSearch,
            new { page = 1, pageSize = 100 });
        Assert.True(accounts.IsSuccess);
        Assert.Equal(5, accounts.Data!.Items![0].Id);
        Assert.True(accounts.Data.Items[0].IsDefault);
    }

    [Fact]
    public async Task CreateAndSslWriteRoutes_AcceptBodies()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [WebsiteRoutes.Create] = """{}""",
                [WebsiteRoutes.SslCreate] = """{"id":7}""",
                [WebsiteRoutes.SslDelete] = """{}"""
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var create = await client.PostAsync<JsonElement>(
            WebsiteRoutes.Create,
            new
            {
                type = "runtime",
                alias = "新站",
                webSiteGroupID = 2,
                IPV6 = false,
                domains = new[] { new { domain = "new.example.com", port = 80, ssl = false } },
                appType = "installed",
                appInstallID = 0,
                runtimeID = 3,
                runtimeType = "php",
                taskID = "task-1",
                runtimeConfig = new { proxyType = "tcp", port = 8080 }
            });
        Assert.True(create.IsSuccess);
        var createBody = server.GetLastRequestBody(WebsiteRoutes.Create);
        Assert.Contains("\"webSiteGroupID\":2", createBody, StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"new.example.com\"", createBody, StringComparison.Ordinal);
        Assert.Contains("\"runtimeType\":\"php\"", createBody, StringComparison.Ordinal);

        var sslCreate = await client.PostAsync<JsonElement>(
            WebsiteRoutes.SslCreate,
            new
            {
                primaryDomain = "cert.example.com",
                otherDomains = new[] { "www.cert.example.com" },
                provider = "http",
                acmeAccountId = 5,
                keyType = "P256",
                apply = true
            });
        Assert.True(sslCreate.IsSuccess);
        Assert.Equal(7, sslCreate.Data.GetProperty("id").GetInt64());
        Assert.Contains("\"acmeAccountId\":5", server.GetLastRequestBody(WebsiteRoutes.SslCreate), StringComparison.Ordinal);

        var sslDelete = await client.PostAsync<JsonElement>(WebsiteRoutes.SslDelete, new { id = 7 });
        Assert.True(sslDelete.IsSuccess);
        Assert.Contains("\"id\":7", server.GetLastRequestBody(WebsiteRoutes.SslDelete), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusinessError_MapsToValidationError()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            RawJsonRoutes = new Dictionary<string, string>
            {
                [WebsiteRoutes.Operate] = """{"code":400,"message":"网站不存在","data":null}"""
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.PostAsync<JsonElement>(WebsiteRoutes.Operate, new { id = 999, operate = "start" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Validation, result.Error?.Kind);
        Assert.Contains("网站不存在", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceAgainstFakePanel_ListsWebsites_WithGroupName()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [WebsiteRoutes.GroupsSearch] = """[{"id":2,"name":"项目A","type":"website","isDefault":false}]"""
            }
        });
        await server.StartAsync();

        var node = new NodeRecord(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            null,
            "cipher",
            "hint",
            AuthMode.HmacSha256,
            null,
            null,
            TlsPolicy.Strict,
            null,
            [],
            null,
            true,
            "v2.3.2",
            0,
            NodeHealth.Online,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var factory = new PanelClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(3) },
            TimeProvider.System);
        var service = new WebsiteService(
            new SingleNodeStore(node),
            new FakeProtector(),
            factory,
            NullLogger<WebsiteService>.Instance);

        var result = await service.ListAsync(new WebsiteQuery(node.Id.Value, null, null, 1, 20), CancellationToken.None);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("项目A", result.Rows[0].GroupName);
        Assert.Single(result.Groups);
    }

    [Fact]
    public void Routes_AreStable()
    {
        Assert.Equal("api/v2/websites/search", WebsiteRoutes.Search);
        Assert.Equal("api/v2/websites/list", WebsiteRoutes.List);
        Assert.Equal("api/v2/groups/search", WebsiteRoutes.GroupsSearch);
        Assert.Equal("api/v2/websites/update", WebsiteRoutes.Update);
        Assert.Equal("api/v2/websites/operate", WebsiteRoutes.Operate);
        Assert.Equal("api/v2/websites/domains", WebsiteRoutes.DomainCreate);
        Assert.Equal("api/v2/websites/domains/update", WebsiteRoutes.DomainUpdate);
        Assert.Equal("api/v2/websites/domains/del", WebsiteRoutes.DomainDelete);
        Assert.Equal("api/v2/websites/proxies", WebsiteRoutes.Proxies);
        Assert.Equal("api/v2/websites/proxies/update", WebsiteRoutes.ProxyUpdate);
        Assert.Equal("api/v2/websites/proxies/delete", WebsiteRoutes.ProxyDelete);
        Assert.Equal("api/v2/websites/proxies/status", WebsiteRoutes.ProxyStatus);
        Assert.Equal("api/v2/websites/ssl/list", WebsiteRoutes.SslList);
        Assert.Equal("api/v2/websites/ssl/search", WebsiteRoutes.SslSearch);
        Assert.Equal("api/v2/websites/ssl/obtain", WebsiteRoutes.SslObtain);
        Assert.Equal("api/v2/websites", WebsiteRoutes.Create);
        Assert.Equal("api/v2/websites/check", WebsiteRoutes.CreateCheck);
        Assert.Equal("api/v2/websites/options", WebsiteRoutes.CreateOptions);
        Assert.Equal("api/v2/websites/ssl", WebsiteRoutes.SslCreate);
        Assert.Equal("api/v2/websites/ssl/del", WebsiteRoutes.SslDelete);
        Assert.Equal("api/v2/websites/acme/search", WebsiteRoutes.AcmeSearch);
        Assert.Equal("api/v2/runtimes/search", WebsiteRoutes.RuntimesSearch);
        Assert.Equal("api/v2/websites/11", WebsiteRoutes.Detail(11));
        Assert.Equal("api/v2/websites/domains/11", WebsiteRoutes.Domains(11));
        Assert.Equal("api/v2/websites/11/https", WebsiteRoutes.Https(11));
    }

    private static IPanelClient CreateClient(FakePanelServer server)
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(3) },
            TimeProvider.System);

        return factory.Create(new NodeDescriptor(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            "test-api-key",
            AuthMode.HmacSha256));
    }

    private sealed class SingleNodeStore : INodeStore
    {
        private readonly NodeRecord _node;

        public SingleNodeStore(NodeRecord node) => _node = node;

        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NodeRecord>>([_node]);

        public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NodeId>>([_node.Id]);

        public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
            Task.FromResult<NodeRecord?>(_node.Id == id ? _node : null);

        public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task AddAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeProtector : INodeSecretProtector
    {
        public ProtectedSecret Protect(string apiKey) => new("cipher", "hint");

        public string Unprotect(string cipherText) => "test-api-key";
    }
}
