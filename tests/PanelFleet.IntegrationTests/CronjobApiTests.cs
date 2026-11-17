using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Cronjobs;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P9 计划任务集成测试：PanelFleetWebApplicationFactory + AdditionalServices 注册假 IPanelClientFactory，
/// 覆盖只读端点、预览→确认→执行→审计、批量上限拒绝、跨节点批量失败停止。
/// </summary>
public sealed class CronjobApiTests
{
    [Fact]
    public async Task Cronjobs_WithoutAuthentication_Returns401()
    {
        using var factory = CreateHost(out _);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/cronjobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cronjobs_ListDetailRecordsAndLog_WorkForAdmin()
    {
        using var factory = CreateHost(out var fake);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");

        using var list = await GetJsonAsync(client, "/api/cronjobs");
        Assert.Equal(2, list.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(1, list.RootElement.GetProperty("nodesSucceeded").GetInt32());
        var first = list.RootElement.GetProperty("rows").EnumerateArray().First();
        Assert.Equal("daily-backup", first.GetProperty("name").GetString());
        Assert.Equal("每周一 01:30", first.GetProperty("specText").GetString());

        using var groups = await GetJsonAsync(client, "/api/cronjobs/groups");
        Assert.Equal(JsonValueKind.Array, groups.RootElement.ValueKind);
        Assert.Contains(
            groups.RootElement.EnumerateArray(),
            group => group.GetProperty("name").GetString() == "备份");

        using var detail = await GetJsonAsync(client, $"/api/cronjobs/{nodeId}/1");
        Assert.Equal("daily-backup", detail.RootElement.GetProperty("name").GetString());
        Assert.True(detail.RootElement.GetProperty("secretSet").GetBoolean());
        Assert.False(detail.RootElement.GetProperty("fields").TryGetProperty("secret", out _));

        using var records = await GetJsonAsync(client, $"/api/cronjobs/{nodeId}/1/records");
        Assert.Equal(1, records.RootElement.GetProperty("total").GetInt32());
        var record = records.RootElement.GetProperty("rows").EnumerateArray().First();
        Assert.Equal("成功", record.GetProperty("statusText").GetString());

        using var log = await GetJsonAsync(client, $"/api/cronjobs/{nodeId}/records/11/log");
        Assert.Contains("backup done", log.RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);

        using var preview = await GetJsonAsync(
            client,
            "/api/cronjobs/spec/next?spec=" + Uri.EscapeDataString("30 2 * * *"));
        Assert.Equal(2, preview.RootElement.GetProperty("nextTimes").GetArrayLength());

        Assert.Contains(fake.CallsFor(CronjobRoutes.Search), call => call.NodeName == "node-a");
    }

    [Fact]
    public async Task CronjobDelete_PreviewConfirmExecute_CallsPanelAndWritesAudit()
    {
        using var factory = CreateHost(out var fake);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");

        var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new
            {
                kind = "cronjob.delete",
                nodeIds = new[] { nodeId },
                parameters = new System.Text.Json.Nodes.JsonObject
                {
                    ["cronjob"] = 1,
                    ["cleanData"] = false
                }
            },
            token,
            cookie);

        var previewBody = await preview.Content.ReadAsStringAsync();
        Assert.True(preview.StatusCode == HttpStatusCode.OK, previewBody);
        using var previewDocument = JsonDocument.Parse(previewBody);
        var root = previewDocument.RootElement;
        Assert.Equal("High", root.GetProperty("risk").GetString());
        Assert.True(root.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Equal("node-a", root.GetProperty("confirmText").GetString());
        var tokenId = root.GetProperty("tokenId").GetString();

        var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId}/execute",
            new { typedConfirmation = "node-a" },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.OK, execution.StatusCode);
        using var executionDocument = JsonDocument.Parse(await execution.Content.ReadAsStringAsync());
        Assert.Equal("Succeeded", executionDocument.RootElement.GetProperty("status").GetString());

        var deleteCall = Assert.Single(fake.CallsFor(CronjobRoutes.Delete));
        Assert.Contains("\"ids\":[1]", deleteCall.Body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var entries = await audit.ListAsync(50, "operation.executed", null, CancellationToken.None);
        Assert.Contains(entries, entry => entry.DetailJson?.Contains("cronjob.delete", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task CronjobBatchPreview_OverLimit_Returns400()
    {
        using var factory = CreateHost(out _);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");
        var ids = Enumerable.Range(1, 21).ToArray();

        var response = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new
            {
                kind = "cronjob.delete",
                nodeIds = new[] { nodeId },
                parameters = new { ids }
            },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("最多操作", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CronjobBatchPreview_AcceptsTypedConfirmationText()
    {
        using var factory = CreateHost(out _);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var first = await CreateNodeAsync(client, token, cookie, "node-a");
        var second = await CreateNodeAsync(client, token, cookie, "node-b");

        var response = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new
            {
                kind = "cronjob.delete",
                nodeIds = new[] { first, second },
                parameters = new
                {
                    cronjobsByNode = new Dictionary<string, int[]>
                    {
                        [first.ToString("D")] = [1],
                        [second.ToString("D")] = [2]
                    }
                }
            },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Critical", document.RootElement.GetProperty("risk").GetString());
        Assert.Equal("确认删除2个节点共2个计划任务", document.RootElement.GetProperty("confirmText").GetString());
    }

    [Fact]
    public async Task CronjobBatchDelete_SecondNodeFails_StopsAndReportsPartial()
    {
        using var factory = CreateHost(out var fake);
        fake.FailNode("node-b", new PanelError(PanelErrorKind.Network, "连接超时"));
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var first = await CreateNodeAsync(client, token, cookie, "node-a");
        var second = await CreateNodeAsync(client, token, cookie, "node-b");

        var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new
            {
                kind = "cronjob.delete",
                nodeIds = new[] { first, second },
                parameters = new
                {
                    cronjobsByNode = new Dictionary<string, int[]>
                    {
                        [first.ToString("D")] = [1],
                        [second.ToString("D")] = [2]
                    }
                }
            },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        var tokenId = previewDocument.RootElement.GetProperty("tokenId").GetString();
        var confirmText = previewDocument.RootElement.GetProperty("confirmText").GetString();

        var execution = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId}/execute",
            new { typedConfirmation = confirmText },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.OK, execution.StatusCode);
        using var executionDocument = JsonDocument.Parse(await execution.Content.ReadAsStringAsync());
        var root = executionDocument.RootElement;
        Assert.Equal("PartiallySucceeded", root.GetProperty("status").GetString());
        var steps = root.GetProperty("steps").EnumerateArray().ToArray();
        Assert.True(steps[0].GetProperty("success").GetBoolean());
        Assert.False(steps[1].GetProperty("success").GetBoolean());
        // node-a 成功提交 1 次；node-b 的调用被假面板记录后返回网络错误。
        Assert.Equal(2, fake.CallsFor(CronjobRoutes.Delete).Count);
    }

    [Fact]
    public async Task CronjobsPage_RendersForAdmin()
    {
        using var factory = CreateHost(out _);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/cronjobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("计划任务", html, StringComparison.Ordinal);
    }

    private static PanelFleetWebApplicationFactory CreateHost(out CronjobFakePanelFactory fake)
    {
        var factory = new PanelFleetWebApplicationFactory();
        var panelFactory = new CronjobFakePanelFactory();
        factory.AdditionalServices = services =>
        {
            services.RemoveAll<IPanelClientFactory>();
            services.AddSingleton<IPanelClientFactory>(panelFactory);
        };
        fake = panelFactory;
        return factory;
    }

    private static async Task<Guid> CreateNodeAsync(
        HttpClient client,
        string token,
        string cookie,
        string name)
    {
        var response = await PostJsonAsync(
            client,
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string url,
        object body,
        string token,
        string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request);
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

/// <summary>集成测试用假 1Panel 客户端工厂（不改共享 FakePanelClientFactory）。</summary>
internal sealed class CronjobFakePanelFactory : IPanelClientFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, string> _payloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PanelError> _nodeFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CronjobPanelCall> _calls = [];

    public CronjobFakePanelFactory()
    {
        _payloads[CronjobRoutes.GroupsSearch] = """
            [{"id":7,"name":"备份","type":"cronjob","isDefault":true},{"id":1,"name":"Default","type":"website","isDefault":true}]
            """;
        _payloads[CronjobRoutes.Search] = """
            {"total":2,"items":[
              {"id":1,"name":"daily-backup","type":"shell","groupID":7,"specCustom":false,"spec":"30 1 * * 1","executor":"bash","scriptMode":"input","status":"Enable","lastRecordStatus":"Success","lastRecordTime":"2026-11-17 01:30:00","retainCopies":7,"retryTimes":1,"timeout":3600,"ignoreErr":false},
              {"id":2,"name":"weekly-site","type":"website","groupID":0,"specCustom":false,"spec":"0 2 * * *","website":"example.com","status":"Disable","lastRecordStatus":"Failed","lastRecordTime":"2026-11-16 02:30:00","retainCopies":3,"retryTimes":0,"timeout":1800,"ignoreErr":true}
            ]}
            """;
        _payloads[CronjobRoutes.Info] = """
            {"id":1,"name":"daily-backup","type":"shell","groupID":7,"specCustom":false,"spec":"30 1 * * 1","executor":"bash","scriptMode":"input","script":"echo hi","containerName":"","user":"","appID":"","website":"","dbType":"","dbName":"","url":"","sourceDir":"","retainCopies":7,"retryTimes":1,"timeout":3600,"ignoreErr":false,"args":"","secret":"secret-value"}
            """;
        _payloads[CronjobRoutes.RecordsSearch] = """
            {"total":1,"items":[{"id":11,"cronjobID":1,"taskID":"task-1","startTime":"2026-11-17 01:30:00","interval":12.5,"status":"Success","message":"","targetPath":"/tmp"}]}
            """;
        _payloads[CronjobRoutes.RecordLog] = "\"backup done\\nfiles: 3\"";
        _payloads[CronjobRoutes.Next] = """["2026-11-17 02:30:00","2026-11-18 02:30:00"]""";
        _payloads[CronjobRoutes.Create] = "{}";
        _payloads[CronjobRoutes.Update] = "{}";
        _payloads[CronjobRoutes.Delete] = "{}";
        _payloads[CronjobRoutes.Status] = "{}";
        _payloads[CronjobRoutes.Handle] = "{}";
    }

    public void FailNode(string nodeName, PanelError error) => _nodeFailures[nodeName] = error;

    public IReadOnlyList<CronjobPanelCall> CallsFor(string path) =>
        Calls.Where(call => call.Path == path).ToArray();

    public IReadOnlyList<CronjobPanelCall> Calls => _calls;

    public IPanelClient Create(NodeDescriptor node) => new CronjobFakePanelClient(this, node);

    private PanelCallResult<T> Handle<T>(string nodeName, string path, string method, object? body)
    {
        lock (_calls)
        {
            _calls.Add(new CronjobPanelCall(
                nodeName,
                path,
                method,
                body is null ? null : JsonSerializer.Serialize(body, JsonOptions)));
        }

        if (_nodeFailures.TryGetValue(nodeName, out var error))
        {
            return new PanelCallResult<T>(default, error, TimeSpan.FromMilliseconds(1));
        }

        if (_payloads.TryGetValue(path, out var json))
        {
            return new PanelCallResult<T>(
                JsonSerializer.Deserialize<T>(json, JsonOptions),
                null,
                TimeSpan.FromMilliseconds(1));
        }

        return new PanelCallResult<T>(
            default,
            new PanelError(PanelErrorKind.NotFound, "[" + nodeName + "] 假客户端未实现路径：" + path),
            TimeSpan.FromMilliseconds(1));
    }

    private sealed class CronjobFakePanelClient : IPanelClient
    {
        private readonly CronjobFakePanelFactory _factory;

        public CronjobFakePanelClient(CronjobFakePanelFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.Md5, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node.Name, relativePath, "GET", null));

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node.Name, relativePath, "POST", body));

        public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
            string ioOption = "all",
            string netOption = "all",
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}

internal sealed record CronjobPanelCall(string NodeName, string Path, string Method, string? Body);
