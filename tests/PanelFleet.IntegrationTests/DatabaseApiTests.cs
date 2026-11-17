using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Databases;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Databases;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P8 数据库管理集成测试：真实 Web 主机 + 内核写操作端点 + 自建假 IPanelClientFactory，
/// 覆盖「预览 → 强确认（Critical 删库）→ 执行 → 审计」、创建库（Medium）与只读查询。
/// </summary>
public sealed class DatabaseApiTests
{
    private const string DeleteParametersJson = """
        {"engine":"mysql","instance":"MySQL","from":"remote","id":3,"database":"app_data","forceDelete":false,"deleteBackup":false}
        """;

    private const string CreateParametersJson = """
        {"engine":"mysql","instance":"MySQL","from":"remote","name":"app_new","format":"utf8mb4","collation":"utf8mb4_general_ci","username":"app_user","password":"s3cret","permission":"%","description":"测试库"}
        """;

    [Fact]
    public async Task DeleteDatabase_PreviewCriticalExecute_WritesAudit()
    {
        var panel = new DatabaseFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "db-node");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "database.delete", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(DeleteParametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Critical", preview.RootElement.GetProperty("risk").GetString());
        Assert.True(preview.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Equal("app_data", preview.RootElement.GetProperty("confirmText").GetString());

        var tokenId = preview.RootElement.GetProperty("tokenId").GetGuid();
        var rejected = await PostAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "wrong" },
            token,
            cookie);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using var retryPreview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "database.delete", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(DeleteParametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);

        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{retryPreview.RootElement.GetProperty("tokenId").GetGuid():D}/execute",
            new { typedConfirmation = "app_data" },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        Assert.True(execution.RootElement.GetProperty("steps")[0].GetProperty("success").GetBoolean());
        Assert.Equal(1, panel.DeleteCalls);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var executed = await audit.ListAsync(20, "operation.executed", null, CancellationToken.None);
        Assert.Contains(executed, entry =>
            entry.NodeId == new NodeId(nodeId)
            && entry.DetailJson is not null
            && entry.DetailJson.Contains("database.delete", StringComparison.Ordinal));
        var rejections = await audit.ListAsync(20, "operation.rejected", null, CancellationToken.None);
        Assert.Contains(rejections, entry =>
            entry.DetailJson is not null
            && entry.DetailJson.Contains("typed_confirmation_mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateDatabase_Medium_PreviewExecute_Base64Password()
    {
        var panel = new DatabaseFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "db-node");

        using var preview = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "database.create", nodeIds = new[] { nodeId }, parameters = JsonNode.Parse(CreateParametersJson) },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Medium", preview.RootElement.GetProperty("risk").GetString());
        Assert.False(preview.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());

        using var execution = await PostJsonAsync(
            client,
            $"/api/operations/{preview.RootElement.GetProperty("tokenId").GetGuid():D}/execute",
            new { typedConfirmation = (string?)null },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", execution.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, panel.CreateCalls);
        var body = Assert.IsType<string>(panel.LastCreateBody);
        Assert.Contains("\"database\":\"MySQL\"", body, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"czNjcmV0\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var executed = await audit.ListAsync(20, "operation.executed", null, CancellationToken.None);
        Assert.Contains(executed, entry =>
            entry.DetailJson is not null
            && entry.DetailJson.Contains("database.create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadEndpoints_ReturnDatabaseTreeUsersAndGrants()
    {
        var panel = new DatabaseFakePanelClientFactory();
        using var factory = CreateFactory(panel);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "db-node");

        using var overview = await GetJsonAsync(client, $"/api/databases?nodeId={nodeId:D}");
        var node = overview.RootElement.GetProperty("nodes")[0];
        Assert.Equal("db-node", node.GetProperty("nodeName").GetString());
        var instance = node.GetProperty("instances")[0];
        Assert.Equal("MySQL", instance.GetProperty("instance").GetString());
        Assert.Equal("MySql", instance.GetProperty("engine").GetString());
        Assert.Equal(2, instance.GetProperty("databases").GetArrayLength());

        using var users = await GetJsonAsync(client, $"/api/databases/{nodeId:D}/instances/MySQL/users");
        Assert.Equal(1, users.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("app", users.RootElement.GetProperty("items")[0].GetProperty("username").GetString());
        Assert.False(users.RootElement.TryGetProperty("password", out _));

        using var grants = await GetJsonAsync(client, $"/api/databases/{nodeId:D}/instances/MySQL/grants");
        Assert.Equal(1, grants.RootElement.GetProperty("items").GetArrayLength());

        using var status = await GetJsonAsync(
            client,
            $"/api/databases/{nodeId:D}/instances/MySQL/status?engine=mysql&from=remote");
        Assert.False(status.RootElement.GetProperty("supported").GetBoolean());
        Assert.Contains("远程实例", status.RootElement.GetProperty("unsupportedReason").GetString(), StringComparison.Ordinal);
    }

    private static PanelFleetWebApplicationFactory CreateFactory(DatabaseFakePanelClientFactory panel)
    {
        var factory = new PanelFleetWebApplicationFactory();
        factory.AdditionalServices = services =>
        {
            services.AddDatabaseModule();
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
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, "创建节点失败：" + payload);
        using var document = JsonDocument.Parse(payload);
        var idElement = document.RootElement.GetProperty("id");
        var idText = idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : idElement.GetRawText().Trim('"');
        assertValidId(idText);
        return Guid.Parse(idText!);

        static void assertValidId(string? value) =>
            Assert.True(Guid.TryParse(value, out _), "节点 id 不是合法 GUID：" + (value ?? "<null>"));
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
}

/// <summary>集成测试假 1Panel 客户端工厂（数据库相关路由）。</summary>
public sealed class DatabaseFakePanelClientFactory : IPanelClientFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private int _deleteCalls;
    private int _createCalls;

    public int DeleteCalls => Volatile.Read(ref _deleteCalls);

    public int CreateCalls => Volatile.Read(ref _createCalls);

    public string? LastCreateBody { get; private set; }

    public IPanelClient Create(NodeDescriptor node) => new FakeClient(this, node);

    private static PanelCallResult<T> Json<T>(object value) =>
        new(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions), null, TimeSpan.FromMilliseconds(1));

    private static PanelCallResult<T> Empty<T>() =>
        new(JsonSerializer.Deserialize<T>("{}", JsonOptions), null, TimeSpan.FromMilliseconds(1));

    private sealed class FakeClient : IPanelClient
    {
        private readonly DatabaseFakePanelClientFactory _factory;

        public FakeClient(DatabaseFakePanelClientFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Handle<T>(relativePath, body: null, isPost: false));

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Handle<T>(relativePath, body, isPost: true));

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

        private PanelCallResult<T> Handle<T>(string relativePath, object? body, bool isPost)
        {
            var path = relativePath;

            if (path.Contains("databases/db/list/mysql", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new List<DatabaseInstanceDto>
                {
                    new()
                    {
                        Id = 1,
                        Type = "mysql",
                        From = "remote",
                        Database = "MySQL",
                        Version = "8.x",
                        Address = "192.0.2.10"
                    }
                });
            }

            if (path.Contains("databases/db/list/", StringComparison.OrdinalIgnoreCase))
            {
                return new PanelCallResult<T>(default, null, TimeSpan.FromMilliseconds(1));
            }

            if (path.Contains("databases/search", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new PanelFleet.Client.Dto.Resources.PagedItemsDto<DatabaseDbDto>
                {
                    Total = 2,
                    Items =
                    [
                        new DatabaseDbDto
                        {
                            Id = 1,
                            Name = "app",
                            MysqlName = "MySQL",
                            From = "remote",
                            Format = "utf8mb4",
                            Collation = "utf8mb4_general_ci",
                            Description = "业务库"
                        },
                        new DatabaseDbDto
                        {
                            Id = 2,
                            Name = "report",
                            MysqlName = "MySQL",
                            From = "remote",
                            Format = "utf8mb4",
                            Collation = "utf8mb4_0900_ai_ci"
                        }
                    ]
                });
            }

            if (path.Contains("databases/users/search", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new List<DatabaseUserDto>
                {
                    new() { Username = "app", Host = "%", Description = "业务账号" }
                });
            }

            if (path.Contains("databases/grants/search", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new List<DatabaseGrantDto>
                {
                    new() { Database = "app", Username = "app", Host = "%" }
                });
            }

            if (path.Contains("databases/format/options", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new List<DatabaseFormatOptionDto>
                {
                    new() { Format = "utf8mb4", Collations = ["utf8mb4_general_ci", "utf8mb4_0900_ai_ci"] }
                });
            }

            if (path.Contains("databases/del/check", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new List<DatabaseLinkedResourceDto>());
            }

            if (path.Contains("/databases/del", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("databases/del", StringComparison.OrdinalIgnoreCase)
                || path.Contains("pg/del", StringComparison.OrdinalIgnoreCase)
                || path.Contains("mongodb/del", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _factory._deleteCalls);
                return Empty<T>();
            }

            if (isPost && (path.EndsWith("api/v2/databases", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/databases/pg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/databases/mongodb", StringComparison.OrdinalIgnoreCase)))
            {
                Interlocked.Increment(ref _factory._createCalls);
                _factory.LastCreateBody = body is null ? null : JsonSerializer.Serialize(body, JsonOptions);
                return Empty<T>();
            }

            if (path.Contains("databases/status", StringComparison.OrdinalIgnoreCase))
            {
                return Json<T>(new DatabaseStatusDto
                {
                    ThreadsConnected = "5",
                    Uptime = "3600"
                });
            }

            return new PanelCallResult<T>(
                default,
                new PanelError(PanelErrorKind.NotFound, "[" + Node.Name + "] 假客户端未实现路径：" + relativePath),
                TimeSpan.FromMilliseconds(1));
        }
    }
}




