using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Websites;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P7 网站管理集成测试：真实 Web 主机 + 内核写操作端点 + 自建假 IPanelClientFactory，
/// 验证 预览 → 强确认 → 执行 → 审计 全链路与只读服务（不访问真实 1Panel）。
/// </summary>
public sealed class WebsiteApiTests
{
    private const string StartParametersJson = """{"websiteId":11,"name":"example.com"}""";

    private const string UpdateParametersJson = """{"websiteId":11,"name":"example.com","primaryDomain":"new.example.com","remark":"主站","webSiteGroupId":1,"favorite":true}""";

    private const string DomainDeleteParametersJson = """{"websiteId":11,"name":"example.com","domainId":21,"domain":"old.example.com"}""";

    private const string ProxyDeleteParametersJson = """{"websiteId":11,"name":"example.com","proxyName":"api"}""";

    private const string SslDeployParametersJson = """{"websiteId":11,"name":"example.com","sslId":2,"httpConfig":"HTTPToHTTPS"}""";

    [Fact]
    public async Task WebsiteStart_PreviewExecute_Medium_AndAudits()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "site-node");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "website.start", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(StartParametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Medium", preview.RootElement.GetProperty("risk").GetString());
        Assert.False(preview.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Contains("启动网站 example.com", preview.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = (string?)null },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        var step = execution.RootElement.GetProperty("steps")[0];
        Assert.True(step.GetProperty("success").GetBoolean());
        Assert.Equal("11", step.GetProperty("targetId").GetString());
        Assert.Contains("\"operate\":\"start\"", panel.LastBody("websites/operate"), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var entries = await audit.ListAsync(50, "operation.executed", null, default);
        var entry = Assert.Single(entries);
        Assert.Equal("website", entry.TargetType);
        Assert.Equal("11", entry.TargetId);
        Assert.Contains("\"kind\":\"website.start\"", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DomainDelete_RequiresTypedConfirmation_AndAuditsRejection()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "danger-node");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "domain.delete", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(DomainDeleteParametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("High", preview.RootElement.GetProperty("risk").GetString());
        Assert.True(preview.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Equal("danger-node", preview.RootElement.GetProperty("confirmText").GetString());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        var rejected = await PostAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "wrong" },
            token,
            cookie);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var rejectedEntries = await audit.ListAsync(50, "operation.rejected", null, default);
        Assert.Contains(
            rejectedEntries,
            entry => entry.DetailJson?.Contains("typed_confirmation_mismatch", StringComparison.Ordinal) == true);

        using var secondPreview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "domain.delete", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(DomainDeleteParametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);
        var secondToken = secondPreview.RootElement.GetProperty("tokenId").GetGuid();

        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{secondToken:D}/execute",
            new { typedConfirmation = "danger-node" },
            token,
            cookie,
            HttpStatusCode.OK);
        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        Assert.Contains("\"id\":21", panel.LastBody("websites/domains/del"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyDelete_And_SslDeploy_ExecuteWithExpectedBodies()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "ops-node");

        _ = await ExecuteOperationAsync(
            client,
            token,
            cookie,
            nodeId,
            "proxy.delete",
            ProxyDeleteParametersJson,
            confirmText: "ops-node");
        Assert.Contains("\"name\":\"api\"", panel.LastBody("websites/proxies/delete"), StringComparison.Ordinal);

        _ = await ExecuteOperationAsync(
            client,
            token,
            cookie,
            nodeId,
            "ssl.deploy",
            SslDeployParametersJson,
            confirmText: "ops-node");
        var sslBody = panel.LastBody("websites/11/https");
        Assert.Contains("\"websiteSSLId\":2", sslBody, StringComparison.Ordinal);
        Assert.Contains("\"httpConfig\":\"HTTPToHTTPS\"", sslBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebsiteUpdate_ExecutesAndAudits()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "update-node");

        var execution = await ExecuteOperationAsync(
            client,
            token,
            cookie,
            nodeId,
            "website.update",
            UpdateParametersJson,
            confirmText: null);

        Assert.Equal("Succeeded", execution.GetProperty("status").GetString());
        var body = panel.LastBody("websites/update");
        Assert.Contains("\"primaryDomain\":\"new.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"favorite\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchWebsiteStart_IsCritical_AndWritesBatchAudit()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeA = await CreateNodeAsync(client, token, cookie, "node-a");
        var nodeB = await CreateNodeAsync(client, token, cookie, "node-b");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new
            {
                kind = "website.start",
                nodeIds = new[] { nodeA, nodeB },
                parameters = JsonNode.Parse(StartParametersJson)
            },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Critical", preview.RootElement.GetProperty("risk").GetString());
        Assert.Equal("2 个节点", preview.RootElement.GetProperty("confirmText").GetString());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "2 个节点" },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, panel.CallsForPath("websites/operate"));

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var batchEntries = await audit.ListAsync(50, "operation.batch", null, default);
        var batch = Assert.Single(batchEntries);
        Assert.Contains("\"nodeCount\":2", batch.DetailJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebsiteReadService_ReturnsMappedData_FromFakePanel()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "read-node");

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebsiteService>();

        var list = await service.ListAsync(new WebsiteQuery(nodeId, null, null, 1, 20), default);
        Assert.Equal(2, list.Rows.Count);
        Assert.Equal("example.com", list.Rows[0].PrimaryDomain);
        Assert.Equal("默认分组", list.Rows[0].GroupName);

        var detail = await service.GetDetailAsync(nodeId, 11, default);
        Assert.Equal("example.com", detail.PrimaryDomain);
        Assert.Equal(2, detail.Domains.Count);

        var domains = await service.GetDomainsAsync(nodeId, 11, default);
        Assert.Equal(2, domains.Count);

        var proxies = await service.GetProxiesAsync(nodeId, 11, default);
        Assert.Equal("api", Assert.Single(proxies).Name);

        var https = await service.GetHttpsAsync(nodeId, 11, default);
        Assert.True(https.Enable);
        Assert.NotNull(https.Certificate);

        var certificates = await service.GetSslCertificatesAsync(nodeId, default);
        Assert.Single(certificates);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", JsonSerializer.Serialize(certificates), StringComparison.Ordinal);

        var runtimes = await service.GetPhpRuntimesAsync(nodeId, default);
        Assert.Equal("8.0.30", Assert.Single(runtimes).Version);
    }

    [Fact]
    public async Task WebsitesPage_RendersForAdmin()
    {
        using var factory = CreateFactory(new WebsiteFakePanelClientFactory());
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/websites");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("网站管理", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebsitesPage_WithNode_RendersWorkbenchTreeAndDeepLinkedList()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "page-node");

        var response = await client.GetAsync("/websites");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("网站管理", html, StringComparison.Ordinal);
        Assert.Contains("page-node", html, StringComparison.Ordinal);
        Assert.Contains("workbench", html, StringComparison.Ordinal);
        Assert.Contains("nav-tree", html, StringComparison.Ordinal);

        // ?nodeId=&id= 深链在首次预渲染阶段即加载网站列表，避免只能依赖交互式 circuit。
        var deepLink = await client.GetAsync($"/websites?nodeId={nodeId:D}&id=11");
        Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
        var deepHtml = System.Net.WebUtility.HtmlDecode(await deepLink.Content.ReadAsStringAsync());
        Assert.Contains("example.com", deepHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateHelpers_Endpoints_ReturnMappedData()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "create-node");

        using var optionsResponse = await client.GetAsync($"/api/websites/{nodeId:D}/create-options");
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        using var options = JsonDocument.Parse(await optionsResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, options.RootElement.GetArrayLength());
        Assert.Equal("a.example.com", options.RootElement[0].GetProperty("primaryDomain").GetString());

        using var precheckResponse = await client.GetAsync($"/api/websites/{nodeId:D}/precheck");
        Assert.Equal(HttpStatusCode.OK, precheckResponse.StatusCode);
        using var precheck = JsonDocument.Parse(await precheckResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, precheck.RootElement.GetArrayLength());

        using var acmeResponse = await client.GetAsync($"/api/websites/{nodeId:D}/acme-accounts");
        Assert.Equal(HttpStatusCode.OK, acmeResponse.StatusCode);
        using var acme = JsonDocument.Parse(await acmeResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, acme.RootElement.GetArrayLength());
        Assert.Equal("ops@example.com", acme.RootElement[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task CreatePreviewExecute_RequiresDomainNodeConfirmation_AndAudits()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "create-node");

        const string parametersJson = """{"type":"static","primaryDomain":"new.example.com","alias":"新站","webSiteGroupId":1,"port":80}""";

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "website.create", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(parametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("High", preview.RootElement.GetProperty("risk").GetString());
        Assert.True(preview.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Equal("new.example.com @ create-node", preview.RootElement.GetProperty("confirmText").GetString());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        var rejected = await PostAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "wrong" },
            token,
            cookie);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using var secondPreview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "website.create", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(parametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);
        var secondToken = secondPreview.RootElement.GetProperty("tokenId").GetGuid();
        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{secondToken:D}/execute",
            new { typedConfirmation = "new.example.com @ create-node" },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        var body = panel.LastBody("api/v2/websites");
        Assert.Contains("\"type\":\"static\"", body, StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"new.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"appType\":\"installed\"", body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var entries = await audit.ListAsync(50, "operation.executed", null, default);
        var entry = Assert.Single(entries);
        Assert.Equal("website", entry.TargetType);
        Assert.Contains("website.create", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslRequestAndDisable_ExecuteWithExpectedBodies()
    {
        var panel = new WebsiteFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "ssl-node");

        const string requestJson = """{"primaryDomain":"cert.example.com","provider":"http","acmeAccountId":5,"keyType":"P256"}""";
        using var requestPreview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "ssl.request", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(requestJson) },
            token,
            cookie,
            HttpStatusCode.OK);
        Assert.Equal("High", requestPreview.RootElement.GetProperty("risk").GetString());
        Assert.Equal("cert.example.com @ ssl-node", requestPreview.RootElement.GetProperty("confirmText").GetString());

        var requestToken = requestPreview.RootElement.GetProperty("tokenId").GetGuid();
        using var requestExecution = await PostJsonAsync(
            client,
            $"/api/operations/{requestToken:D}/execute",
            new { typedConfirmation = "cert.example.com @ ssl-node" },
            token,
            cookie,
            HttpStatusCode.OK);
        Assert.Equal("Succeeded", requestExecution.RootElement.GetProperty("status").GetString());
        Assert.Contains("\"ID\":7", panel.LastBody("websites/ssl/obtain"), StringComparison.Ordinal);

        const string disableJson = """{"websiteId":11,"name":"example.com"}""";
        using var disablePreview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "ssl.disable", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(disableJson) },
            token,
            cookie,
            HttpStatusCode.OK);
        Assert.Equal("High", disablePreview.RootElement.GetProperty("risk").GetString());

        var disableToken = disablePreview.RootElement.GetProperty("tokenId").GetGuid();
        using var disableExecution = await PostJsonAsync(
            client,
            $"/api/operations/{disableToken:D}/execute",
            new { typedConfirmation = "ssl-node" },
            token,
            cookie,
            HttpStatusCode.OK);
        Assert.Equal("Succeeded", disableExecution.RootElement.GetProperty("status").GetString());
        Assert.Contains("\"enable\":false", panel.LastBody("websites/11/https"), StringComparison.Ordinal);
    }

    private static async Task<JsonElement> ExecuteOperationAsync(
        HttpClient client,
        string token,
        string cookie,
        Guid nodeId,
        string kind,
        string parametersJson,
        string? confirmText)
    {
        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind, nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(parametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);
        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();

        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = confirmText },
            token,
            cookie,
            HttpStatusCode.OK);
        return execution.RootElement.Clone();
    }

    private static PanelFleetWebApplicationFactory CreateFactory(WebsiteFakePanelClientFactory panel)
    {
        var factory = new PanelFleetWebApplicationFactory();
        factory.AdditionalServices = services =>
        {
            services.AddWebsiteModule();
            services.RemoveAll<IPanelClientFactory>();
            services.AddSingleton<IPanelClientFactory>(panel);
        };

        return factory;
    }

    private static async Task<Guid> CreateNodeAsync(
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
        return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<JsonDocument> PostJsonAsync(
        HttpClient client,
        string url,
        object body,
        string token,
        string cookie,
        HttpStatusCode expected)
    {
        var response = await PostAsync(client, url, body, token, cookie);
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        object body,
        string token,
        string cookie) =>
        await client.SendAsync(CreateJsonRequest(HttpMethod.Post, url, body, token, cookie));

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

    /// <summary>网站管理集成测试用假面板客户端工厂：按方法 + 路径返回假数据，按节点模拟网络失败，并记录请求体。</summary>
    private sealed class WebsiteFakePanelClientFactory : IPanelClientFactory
    {
        private readonly ConcurrentDictionary<string, string> _lastBodies = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failingNodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public void FailNode(string nodeName)
        {
            lock (_lock)
            {
                _failingNodes.Add(nodeName);
            }
        }

        public int CallsForPath(string pathContains) =>
            _calls.Where(pair => pair.Key.Contains(pathContains, StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value);

        public string LastBody(string pathContains)
        {
            var match = _lastBodies
                .Where(pair => pair.Key.Contains(pathContains, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.Key.Length)
                .FirstOrDefault();
            return match.Value ?? string.Empty;
        }

        public IPanelClient Create(NodeDescriptor node) => new FakeClient(this, node);

        private bool IsFailing(NodeDescriptor node)
        {
            lock (_lock)
            {
                return _failingNodes.Contains(node.Name);
            }
        }

        private void Record(string path, object? body)
        {
            _calls.AddOrUpdate(path, 1, static (_, count) => count + 1);
            if (body is not null)
            {
                _lastBodies[path] = JsonSerializer.Serialize(body);
            }
        }

        private PanelCallResult<T> Respond<T>(NodeDescriptor node, string method, string path, object? body)
        {
            Record(path, body);

            if (IsFailing(node))
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(PanelErrorKind.Network, $"[{node.Name}] 无法连接节点"),
                    TimeSpan.FromMilliseconds(2));
            }

            var json = Payload(method, path);
            if (json is null)
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(PanelErrorKind.NotFound, $"[{node.Name}] 假客户端未实现路径：{path}"),
                    TimeSpan.FromMilliseconds(2));
            }

            if (string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            {
                return new PanelCallResult<T>(default, null, TimeSpan.FromMilliseconds(2));
            }

            var data = JsonSerializer.Deserialize<T>(json, FakeJson.Options);
            return new PanelCallResult<T>(data, null, TimeSpan.FromMilliseconds(2));
        }

        private static string? Payload(string method, string path)
        {
            var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);

            if (path.Contains("groups/search", StringComparison.OrdinalIgnoreCase))
            {
                return """[{"id":1,"name":"Default","type":"website","isDefault":true}]""";
            }

            if (path.Contains("runtimes/search", StringComparison.OrdinalIgnoreCase))
            {
                return """{"total":1,"items":[{"id":2,"name":"PHP-8030","status":"Running","type":"php","image":"1panel-php-fpm:8.0.30"}]}""";
            }

            if (path.Contains("websites/options", StringComparison.OrdinalIgnoreCase))
            {
                return """[{"id":1,"primaryDomain":"a.example.com","alias":"A"},{"id":2,"primaryDomain":"b.example.com"}]""";
            }

            if (path.Contains("websites/check", StringComparison.OrdinalIgnoreCase))
            {
                return "[]";
            }

            if (path.Contains("acme/search", StringComparison.OrdinalIgnoreCase))
            {
                return """{"total":1,"items":[{"id":5,"email":"ops@example.com","type":"letsencrypt","status":"valid","isDefault":true}]}""";
            }

            if (path.EndsWith("websites/ssl", StringComparison.OrdinalIgnoreCase))
            {
                return """{"id":7}""";
            }

            if (path.Contains("websites/ssl/list", StringComparison.OrdinalIgnoreCase))
            {
                return """[{"id":2,"primaryDomain":"*.example.com","status":"ready","expireDate":"2026-12-26T23:59:59Z","isIP":false,"certPath":"","pushDir":false,"privateKey":"-----BEGIN PRIVATE KEY-----secret","pem":"-----BEGIN CERTIFICATE-----secret"}]""";
            }

            if (path.Contains("websites/ssl/obtain", StringComparison.OrdinalIgnoreCase))
            {
                return "{}";
            }

            if (path.Contains("websites/php/version", StringComparison.OrdinalIgnoreCase))
            {
                return "{}";
            }

            if (path.Contains("websites/domains/del", StringComparison.OrdinalIgnoreCase)
                || path.Contains("websites/domains/update", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("websites/domains", StringComparison.OrdinalIgnoreCase)
                || path.Contains("websites/proxies/update", StringComparison.OrdinalIgnoreCase)
                || path.Contains("websites/proxies/delete", StringComparison.OrdinalIgnoreCase)
                || path.Contains("websites/proxies/status", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("websites/operate", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("websites/update", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("websites", StringComparison.OrdinalIgnoreCase))
            {
                return "{}";
            }

            if (path.Contains("websites/domains/", StringComparison.OrdinalIgnoreCase))
            {
                return """[{"id":21,"websiteId":11,"domain":"example.com","port":80,"ssl":true},{"id":22,"websiteId":11,"domain":"www.example.com","port":81,"ssl":false}]""";
            }

            if (path.Contains("websites/proxies", StringComparison.OrdinalIgnoreCase))
            {
                return """[{"id":11,"enable":true,"cache":false,"cacheTime":0,"cacheUnit":"","serverCacheTime":10,"serverCacheUnit":"m","name":"api","match":"/api","proxyPass":"http://127.0.0.1:8080","proxyHost":"$host","sni":false,"proxySSLName":"","sslVerify":false,"cors":false,"allowOrigins":"","allowMethods":"","allowHeaders":"","allowCredentials":false,"preflight":false}]""";
            }

            if (path.Contains("/https", StringComparison.OrdinalIgnoreCase))
            {
                if (!isGet)
                {
                    return "{}";
                }

                return """{"enable":true,"httpConfig":"HTTPToHTTPS","SSL":{"id":2,"primaryDomain":"*.example.com","status":"ready","expireDate":"2026-12-26T23:59:59Z","isIP":false,"privateKey":"-----BEGIN PRIVATE KEY-----secret","pem":"-----BEGIN CERTIFICATE-----secret"},"SSLProtocol":["TLSv1.2","TLSv1.3"],"algorithm":"","hsts":false,"hstsIncludeSubDomains":false,"httpsPort":"443","httpsPorts":[443],"http3":false}""";
            }

            if (path.Contains("websites/search", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    {"total":2,"items":[{"id":11,"primaryDomain":"example.com","alias":"example","type":"static","status":"Running","protocol":"HTTPS","expireDate":"2027-01-01T00:00:00Z","sitePath":"/1panel/www/sites/example.com","favorite":true,"sslStatus":"ready"},{"id":12,"primaryDomain":"blog.example.com","alias":"blog","type":"proxy","status":"Stopped","protocol":"HTTP","expireDate":"9999-12-31T00:00:00Z","sitePath":"/1panel/www/sites/blog.example.com","favorite":false}]}
                    """;
            }

            if (isGet && path.Contains("websites/", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    {"id":11,"createdAt":"2026-01-01T00:00:00Z","protocol":"HTTPS","primaryDomain":"example.com","type":"static","alias":"example","remark":"主站","status":"Running","expireDate":"2027-01-01T00:00:00Z","sitePath":"/1panel/www/sites/example.com","siteDir":"/1panel/www/sites/example.com","runtimeID":2,"runtimeName":"PHP-8030","runtimeType":"php","webSiteGroupId":1,"favorite":true,"IPV6":true,"accessLog":true,"errorLog":false,"accessLogPath":"/1panel/www/sites/example.com/log/access.log","errorLogPath":"/1panel/www/sites/example.com/log/error.log","domains":["example.com","www.example.com"],"childSites":[]}
                    """;
            }

            return null;
        }

        private sealed class FakeClient : IPanelClient
        {
            private readonly WebsiteFakePanelClientFactory _factory;

            public FakeClient(WebsiteFakePanelClientFactory factory, NodeDescriptor node)
            {
                _factory = factory;
                Node = node;
            }

            public NodeDescriptor Node { get; }

            public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

            public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(_factory.Respond<T>(Node, "GET", relativePath, null));

            public Task<PanelCallResult<T>> PostAsync<T>(
                string relativePath,
                object? body,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(_factory.Respond<T>(Node, "POST", relativePath, body));

            public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new PanelCallResult<OsInfoDto>(null, null, TimeSpan.Zero));

            public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PanelCallResult<DashboardCurrentDto>(null, null, TimeSpan.Zero));

            public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
                string ioOption = "all",
                string netOption = "all",
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PanelCallResult<DashboardBaseDto>(null, null, TimeSpan.Zero));

            public void Dispose()
            {
            }
        }

        private static class FakeJson
        {
            public static readonly JsonSerializerOptions Options = new()
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            };
        }
    }
}
