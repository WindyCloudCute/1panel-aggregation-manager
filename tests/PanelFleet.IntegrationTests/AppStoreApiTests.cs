using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.AppStore;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P5 应用商店集成测试：真实 Web 主机 + 内核写操作端点 + 自建假 IPanelClientFactory，
/// 验证 预览 → 强确认 → 执行 → 审计 全链路、批量失败停止与只读服务（不访问真实 1Panel）。
/// </summary>
public sealed class AppStoreApiTests
{
    private const string InstallParametersJson = """
        {"appDetailId":2064,"name":"mysql-test","appKey":"mysql","appName":"MySQL","version":"8.4.13","params":{"PANEL_APP_PORT_HTTP":3306,"PANEL_DB_ROOT_PASSWORD":"secret-value"},"pullImage":true}
        """;

    private const string ParamsJson = """{"installId":1,"name":"komga","params":{"PANEL_APP_PORT_HTTP":"25601"},"advanced":false}""";

    private const string UpdateParametersJson = """{"installId":1,"name":"komga","detailId":9001,"version":"1.29.0","backup":true,"pullImage":true}""";

    private const string UninstallParametersJson = """{"installId":1,"name":"komga","deleteDB":false,"deleteBackup":false,"deleteImage":false}""";

    [Fact]
    public async Task AppInstall_PreviewExecute_WritesAudit_AndCallsPanelWriteRoute()
    {
        var panel = new AppStoreFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "app-node");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new
            {
                kind = "app.install",
                nodeIds = new[] { nodeId },
                parameters = JsonNode.Parse(InstallParametersJson)
            },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Medium", preview.RootElement.GetProperty("risk").GetString());
        Assert.False(preview.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Contains("安装应用 MySQL", preview.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal("app-node", preview.RootElement.GetProperty("targets")[0].GetProperty("nodeName").GetString());

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
        Assert.False(string.IsNullOrWhiteSpace(step.GetProperty("taskId").GetString()));
        Assert.Contains("安装任务已提交", step.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, panel.CallsForPath("apps/install"));
        Assert.Contains("\"appDetailId\":2064", panel.LastBody("apps/install"), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", step.GetProperty("detailJson").GetString() ?? string.Empty, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var entries = await audit.ListAsync(50, "operation.executed", null, default);
        var entry = Assert.Single(entries);
        Assert.Equal("app", entry.TargetType);
        Assert.Equal("mysql-test", entry.TargetId);
        Assert.Contains("\"kind\":\"app.install\"", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"nodeName\":\"app-node\"", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"taskId\":", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppUninstall_RequiresTypedNodeName_AndAuditsRejection()
    {
        var panel = new AppStoreFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "danger-node");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "app.uninstall", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(UninstallParametersJson) },
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
            new { kind = "app.uninstall", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(UninstallParametersJson) },
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
        Assert.Contains("\"operate\":\"delete\"", panel.LastBody("apps/installed/op"), StringComparison.Ordinal);
        Assert.Contains("\"installId\":1", panel.LastBody("apps/installed/op"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchInstall_IsCritical_AndWritesBatchAudit()
    {
        var panel = new AppStoreFakePanelClientFactory();
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
                kind = "app.install",
                nodeIds = new[] { nodeA, nodeB },
                parameters = JsonNode.Parse(InstallParametersJson)
            },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Critical", preview.RootElement.GetProperty("risk").GetString());
        Assert.Equal("2 个节点", preview.RootElement.GetProperty("confirmText").GetString());
        Assert.Equal(2, preview.RootElement.GetProperty("targets").GetArrayLength());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "2 个节点" },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, panel.CallsForPath("apps/install"));

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var batchEntries = await audit.ListAsync(50, "operation.batch", null, default);
        var batch = Assert.Single(batchEntries);
        Assert.Contains("\"nodeCount\":2", batch.DetailJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"succeeded\":2", batch.DetailJson ?? string.Empty, StringComparison.Ordinal);

        var executed = await audit.ListAsync(50, "operation.executed", null, default);
        Assert.Equal(2, executed.Count);
    }

    [Fact]
    public async Task BatchUpgrade_PartialFailure_StopsAndAuditsFailure()
    {
        var panel = new AppStoreFakePanelClientFactory();
        panel.FailNode("node-b");
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
                kind = "app.update",
                nodeIds = new[] { nodeA, nodeB },
                parameters = JsonNode.Parse(UpdateParametersJson)
            },
            token,
            cookie,
            HttpStatusCode.OK);
        Assert.Equal("Critical", preview.RootElement.GetProperty("risk").GetString());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "2 个节点" },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("PartiallySucceeded", execution.RootElement.GetProperty("status").GetString());
        var steps = execution.RootElement.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.True(steps[0].GetProperty("success").GetBoolean());
        Assert.False(steps[1].GetProperty("success").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var failed = await audit.ListAsync(50, "operation.failed", null, default);
        Assert.Single(failed);
        Assert.Contains("\"kind\":\"app.update\"", failed[0].DetailJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchUpgrade_AcrossNodes_ExecutesSerially_AndAuditsBatch()
    {
        var panel = new AppStoreFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeA = await CreateNodeAsync(client, token, cookie, "node-a");
        var nodeB = await CreateNodeAsync(client, token, cookie, "node-b");
        var parameters = BatchUpgradeParameters(
            (nodeA, 1, "komga", "1.29.0", 9001),
            (nodeA, 2, "uptime-kuma", "1.24.0", 9002),
            (nodeB, 3, "mysql", "8.4.13", 9003));

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "app.batch_upgrade", nodeIds = new[] { nodeA, nodeB }, parameters },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Critical", preview.RootElement.GetProperty("risk").GetString());
        Assert.True(preview.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Equal("升级 2 个节点 3 个应用", preview.RootElement.GetProperty("confirmText").GetString());
        Assert.Equal(2, preview.RootElement.GetProperty("targets").GetArrayLength());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "升级 2 个节点 3 个应用" },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        var steps = execution.RootElement.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.True(steps[0].GetProperty("success").GetBoolean());
        Assert.True(steps[1].GetProperty("success").GetBoolean());
        Assert.Contains("已提交 2 个应用升级任务", steps[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, panel.CallsForPath("apps/installed/op"));
        Assert.Contains("\"installId\":3", panel.LastBody("apps/installed/op"), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var batch = await audit.ListAsync(50, "operation.batch", null, default);
        Assert.Single(batch);
        Assert.Contains("\"nodeCount\":2", batch[0].DetailJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"succeeded\":2", batch[0].DetailJson ?? string.Empty, StringComparison.Ordinal);
        var executed = await audit.ListAsync(50, "operation.executed", null, default);
        Assert.Equal(2, executed.Count);
        Assert.All(executed, entry => Assert.Contains("app.batch_upgrade", entry.DetailJson ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BatchUpgrade_FailureInsideNode_StopsRemainingAppsAndNodes()
    {
        var panel = new AppStoreFakePanelClientFactory();
        panel.FailAfterCalls("node-a", "apps/installed/op", 1);
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeA = await CreateNodeAsync(client, token, cookie, "node-a");
        var nodeB = await CreateNodeAsync(client, token, cookie, "node-b");
        var parameters = BatchUpgradeParameters(
            (nodeA, 1, "komga", "1.29.0", 9001),
            (nodeA, 2, "uptime-kuma", "1.24.0", 9002),
            (nodeB, 3, "mysql", "8.4.13", 9003));

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "app.batch_upgrade", nodeIds = new[] { nodeA, nodeB }, parameters },
            token,
            cookie,
            HttpStatusCode.OK);
        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();

        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "升级 2 个节点 3 个应用" },
            token,
            cookie,
            HttpStatusCode.OK);

        // 节点 A 内第二个应用失败后不再执行节点 B（内核在首个失败节点停止）：总共只调用 2 次。
        Assert.Equal("Failed", execution.RootElement.GetProperty("status").GetString());
        var steps = execution.RootElement.GetProperty("steps");
        Assert.Equal(1, steps.GetArrayLength());
        Assert.False(steps[0].GetProperty("success").GetBoolean());
        Assert.Contains("升级 uptime-kuma 失败", steps[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, panel.CallsForPath("apps/installed/op"));

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var failed = await audit.ListAsync(50, "operation.failed", null, default);
        Assert.Single(failed);
        Assert.Contains("\"kind\":\"app.batch_upgrade\"", failed[0].DetailJson ?? string.Empty, StringComparison.Ordinal);
        var executed = await audit.ListAsync(50, "operation.executed", null, default);
        Assert.Empty(executed);
    }

    [Fact]
    public async Task BatchUpgrade_Preview_MissingTargets_ReturnsValidationError()
    {
        var panel = new AppStoreFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");

        using var response = await PostAsync(
            client,
            "/api/operations/preview",
            new { kind = "app.batch_upgrade", nodeIds = new[] { nodeId }, parameters = new JsonObject { ["backup"] = true } },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("targets", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppParamsUpdate_Execute_PostsParamsAndAudits()
    {
        var panel = new AppStoreFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "params-node");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "app.params_update", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(ParamsJson) },
            token,
            cookie,
            HttpStatusCode.OK);
        Assert.Equal("Medium", preview.RootElement.GetProperty("risk").GetString());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = (string?)null },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        Assert.Contains("\"installId\":1", panel.LastBody("apps/installed/params/update"), StringComparison.Ordinal);
        Assert.Contains("\"params\":", panel.LastBody("apps/installed/params/update"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppStoreReadService_ReturnsMappedData_FromFakePanel()
    {
        var panel = new AppStoreFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "read-node");

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AppStoreService>();

        var installed = await store.SearchInstalledAsync(
            new NodeId(nodeId), name: null, tags: null, updateOnly: false, checkUpdate: false, page: 1, pageSize: 20, default);
        Assert.Equal(2, installed.Total);
        Assert.Equal("komga", installed.Items[0].Name);

        var config = await store.GetInstalledParamsAsync(new NodeId(nodeId), 1, default);
        Assert.Equal(2, config.Params.Count);

        var versions = await store.GetUpdateVersionsAsync(new NodeId(nodeId), 1, default);
        Assert.Single(versions);
        Assert.Equal(9001, versions[0].DetailId);

        var task = await store.GetTaskStatusAsync(new NodeId(nodeId), "task-1", default);
        Assert.NotNull(task);
        Assert.True(task.IsExecuting);

        var resources = await store.GetDeleteCheckAsync(new NodeId(nodeId), 1, default);
        Assert.Empty(resources);
    }

    [Fact]
    public async Task AppStorePage_RendersForAdmin()
    {
        using var factory = CreateFactory(new AppStoreFakePanelClientFactory());
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/appstore");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("应用商店", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppStorePage_WithNode_LoadsStoreAndInstalledData()
    {
        var panel = new AppStoreFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "page-node");

        var response = await client.GetAsync("/appstore");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("应用商店", html, StringComparison.Ordinal);
        Assert.Contains("MySQL", html, StringComparison.Ordinal); // 假面板应用商店搜索结果
        Assert.Contains("page-node", html, StringComparison.Ordinal); // 节点选择
        Assert.Contains("可升级", html, StringComparison.Ordinal); // 升级聚合 tab
    }

    private static PanelFleetWebApplicationFactory CreateFactory(AppStoreFakePanelClientFactory panel)
    {
        var factory = new PanelFleetWebApplicationFactory();
        factory.AdditionalServices = services =>
        {
            services.AddAppStoreModule();
            services.RemoveAll<IPanelClientFactory>();
            services.AddSingleton<IPanelClientFactory>(panel);
        };

        return factory;
    }

    private static JsonObject BatchUpgradeParameters(
        params (Guid NodeId, long InstallId, string Name, string Version, long DetailId)[] targets)
    {
        var array = new JsonArray();
        foreach (var target in targets)
        {
            array.Add(new JsonObject
            {
                ["nodeId"] = target.NodeId.ToString("D"),
                ["installId"] = target.InstallId,
                ["name"] = target.Name,
                ["version"] = target.Version,
                ["detailId"] = target.DetailId
            });
        }

        return new JsonObject
        {
            ["backup"] = true,
            ["pullImage"] = true,
            ["deleteImage"] = false,
            ["targets"] = array
        };
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

    /// <summary>应用商店集成测试用假面板客户端工厂：按路径返回假数据，按节点模拟网络失败，并记录请求体。</summary>
    private sealed class AppStoreFakePanelClientFactory : IPanelClientFactory
    {
        private readonly ConcurrentDictionary<string, string> _lastBodies = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (int AllowedCalls, PanelErrorKind Kind)> _failAfterCalls = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _matchedCalls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failingNodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public void FailNode(string nodeName)
        {
            lock (_lock)
            {
                _failingNodes.Add(nodeName);
            }
        }

        /// <summary>指定节点的指定路径：前 allowedCalls 次成功，之后返回网络错误（用于验证批内失败停止）。</summary>
        public void FailAfterCalls(string nodeName, string pathContains, int allowedCalls)
        {
            _failAfterCalls[$"{nodeName}|{pathContains}"] = (allowedCalls, PanelErrorKind.Network);
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

        private void Record(NodeDescriptor node, string path, object? body)
        {
            _calls.AddOrUpdate(path, 1, static (_, count) => count + 1);
            if (body is not null)
            {
                _lastBodies[path] = JsonSerializer.Serialize(body);
            }
        }

        private PanelCallResult<T> Respond<T>(NodeDescriptor node, string path, object? body)
        {
            Record(node, path, body);

            if (IsFailing(node))
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(PanelErrorKind.Network, $"[{node.Name}] 无法连接节点"),
                    TimeSpan.FromMilliseconds(2));
            }

            foreach (var rule in _failAfterCalls)
            {
                var separator = rule.Key.IndexOf('|');
                if (separator <= 0
                    || !string.Equals(rule.Key[..separator], node.Name, StringComparison.OrdinalIgnoreCase)
                    || !path.Contains(rule.Key[(separator + 1)..], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var matched = _matchedCalls.AddOrUpdate(rule.Key, 1, static (_, count) => count + 1);
                if (matched > rule.Value.AllowedCalls)
                {
                    return new PanelCallResult<T>(
                        default,
                        new PanelError(rule.Value.Kind, $"[{node.Name}] 假面板第 {matched} 次调用失败：{path}"),
                        TimeSpan.FromMilliseconds(2));
                }
            }

            var json = Payload(path);
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

            try
            {
                var data = JsonSerializer.Deserialize<T>(json, FakeJson.Options);
                return new PanelCallResult<T>(data, null, TimeSpan.FromMilliseconds(2));
            }
            catch (JsonException exception)
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(PanelErrorKind.Protocol, $"[{node.Name}] 假数据解析失败：{exception.Message}", Cause: exception),
                    TimeSpan.FromMilliseconds(2));
            }
        }

        private static string? Payload(string path)
        {
            if (path.Contains("apps/installed/op", StringComparison.OrdinalIgnoreCase)
                || path.Contains("apps/installed/params/update", StringComparison.OrdinalIgnoreCase))
            {
                return "{}";
            }

            if (path.Contains("apps/installed/update/versions", StringComparison.OrdinalIgnoreCase))
            {
                return """[{"version":"1.29.0","detailId":9001,"dockerCompose":"services: {}"}]""";
            }

            if (path.Contains("apps/installed/delete/check/", StringComparison.OrdinalIgnoreCase))
            {
                return "null";
            }

            if (path.Contains("apps/installed/params/", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    {"params":[{"key":"PANEL_APP_PORT_HTTP","labelZh":"端口","labelEn":"Port","type":"number","value":"25600","edit":true,"required":true},{"key":"PANEL_DB_ROOT_PASSWORD","labelZh":"root密码","labelEn":"Root Password","type":"password","value":"secret","edit":true,"required":true}],"rawCompose":"","advanced":false,"cpuQuota":0,"memoryLimit":0,"memoryUnit":"M","containerName":"1Panel-komga-2pdN","allowPort":true,"editCompose":false,"hostMode":false,"pullImage":false,"gpuConfig":false,"dockerCompose":"services: {}","webUI":"","type":"website","specifyIP":"","restartPolicy":"always"}
                    """;
            }

            if (path.Contains("apps/installed/info/", StringComparison.OrdinalIgnoreCase))
            {
                return """{"id":1,"name":"komga","version":"1.28.1","status":"Running","message":"","HttpPort":25600,"container":"1Panel-komga-2pdN","composePath":"/1panel/apps/komga/komga/docker-compose.yml","appKey":"komga","appPorts":[25600],"env":{"PANEL_APP_PORT_HTTP":"25600"}}""";
            }

            if (path.Contains("apps/installed/search", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    {"total":2,"items":[{"id":1,"name":"komga","appKey":"komga","appType":"website","appName":"Komga","version":"1.28.1","status":"Running","message":"","httpPort":25600,"httpsPort":0,"path":"/1panel/apps/komga/komga","canUpdate":false,"isEdit":false,"linkDB":false,"favorite":false,"createdAt":"2026-09-10T18:20:38Z","env":{"PANEL_APP_PORT_HTTP":"25600"}},{"id":2,"name":"uptime-kuma","appKey":"uptime-kuma","appType":"website","appName":"Uptime Kuma","version":"1.23.17","status":"Running","message":"","httpPort":9010,"httpsPort":0,"path":"/1panel/apps/uptime-kuma/uptime-kuma","canUpdate":true,"isEdit":false,"linkDB":false,"favorite":false,"createdAt":"0001-01-01T00:00:00Z","env":{}}]}
                    """;
            }

            if (path.Contains("logs/tasks/search", StringComparison.OrdinalIgnoreCase))
            {
                return """{"total":1,"items":[{"id":"task-1","name":"安装应用 [mysql]","type":"App","operate":"TaskInstall","status":"Executing","errorMsg":"","resourceID":5,"currentStep":"","createdAt":"2026-11-16T02:51:00Z","endAt":""}]}""";
            }

            if (path.Contains("apps/search", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    {"total":1,"items":[{"id":221,"name":"MySQL","key":"mysql","description":"Open Source Relational Database","type":"runtime","status":"","installed":false,"limit":0,"recommend":5,"gpuSupport":false,"batchInstallSupport":true,"tags":["Database"]}]}
                    """;
            }

            if (path.Contains("apps/tags", StringComparison.OrdinalIgnoreCase))
            {
                return """[{"id":3,"key":"Database","name":"Database"}]""";
            }

            if (path.Contains("apps/checkupdate", StringComparison.OrdinalIgnoreCase))
            {
                return """{"canUpdate":false,"isSyncing":false,"appStoreLastModified":0}""";
            }

            if (path.Contains("apps/install", StringComparison.OrdinalIgnoreCase))
            {
                return """{"id":5,"name":"mysql-test"}""";
            }

            return null;
        }

        private sealed class FakeClient : IPanelClient
        {
            private readonly AppStoreFakePanelClientFactory _factory;

            public FakeClient(AppStoreFakePanelClientFactory factory, NodeDescriptor node)
            {
                _factory = factory;
                Node = node;
            }

            public NodeDescriptor Node { get; }

            public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

            public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(_factory.Respond<T>(Node, relativePath, null));

            public Task<PanelCallResult<T>> PostAsync<T>(
                string relativePath,
                object? body,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(_factory.Respond<T>(Node, relativePath, body));

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
