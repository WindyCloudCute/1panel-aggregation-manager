using System.Collections.Concurrent;
using System.Text.Json;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Websites;

/// <summary>记录一次假 1Panel 调用（方法/路径/请求体/节点名）。</summary>
internal sealed record RecordedWebsiteCall(string Method, string Path, string? Body, string NodeName);

/// <summary>
/// 网站管理单元测试用假 1Panel 客户端工厂：按路径包含匹配预设 data JSON，
/// 也支持按（路径 + 请求体）动态返回，用于区分同一路径不同分组/参数的查询。
/// </summary>
internal sealed class RecordingWebsiteClientFactory : IPanelClientFactory
{
    private readonly ConcurrentDictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PanelErrorKind> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecordedWebsiteCall> _calls = [];

    /// <summary>动态响应：路径 + 请求体 JSON → data JSON；返回 null 表示按静态响应/未配置处理。</summary>
    public Func<string, string?, string?>? Responder { get; set; }

    public IReadOnlyList<RecordedWebsiteCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    public RecordingWebsiteClientFactory Respond(string pathContains, string dataJson)
    {
        _responses[pathContains] = dataJson;
        return this;
    }

    public RecordingWebsiteClientFactory Fail(string pathContains, PanelErrorKind kind)
    {
        _failures[pathContains] = kind;
        return this;
    }

    public IPanelClient Create(NodeDescriptor node) => new FakeWebsiteClient(this, node);

    internal PanelCallResult<T> Handle<T>(NodeDescriptor node, string method, string path, object? body)
    {
        var bodyText = body is null ? null : JsonSerializer.Serialize(body, WebsiteTestJson.WebOptions);
        lock (_calls)
        {
            _calls.Add(new RecordedWebsiteCall(method, path, bodyText, node.Name));
        }

        foreach (var failure in _failures)
        {
            if (path.Contains(failure.Key, StringComparison.OrdinalIgnoreCase))
            {
                return new PanelCallResult<T>(
                    default,
                    new PanelError(failure.Value, $"[{node.Name}] 假面板错误：{failure.Key}"),
                    TimeSpan.FromMilliseconds(3));
            }
        }

        var dynamicJson = Responder?.Invoke(path, bodyText);
        var match = _responses
            .Where(pair => path.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();
        var json = dynamicJson ?? match.Value;

        if (json is null)
        {
            return new PanelCallResult<T>(
                default,
                new PanelError(PanelErrorKind.NotFound, $"[{node.Name}] 未配置假响应：{path}"),
                TimeSpan.FromMilliseconds(3));
        }

        if (string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
        {
            return new PanelCallResult<T>(default, null, TimeSpan.FromMilliseconds(3));
        }

        var data = JsonSerializer.Deserialize<T>(json, WebsiteTestJson.Options);
        return new PanelCallResult<T>(data, null, TimeSpan.FromMilliseconds(3));
    }

    private sealed class FakeWebsiteClient : IPanelClient
    {
        private readonly RecordingWebsiteClientFactory _factory;

        public FakeWebsiteClient(RecordingWebsiteClientFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node, "GET", relativePath, null));

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory.Handle<T>(Node, "POST", relativePath, body));

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
}

internal sealed class WebsiteFakeNodeStore : INodeStore
{
    public WebsiteFakeNodeStore(params NodeRecord[] nodes) => Nodes = nodes.ToList();

    public List<NodeRecord> Nodes { get; }

    public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeRecord>>(Nodes);

    public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeId>>(Nodes.Where(node => node.IsEnabled).Select(node => node.Id).ToArray());

    public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
        Task.FromResult(Nodes.FirstOrDefault(node => node.Id == id));

    public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task AddAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class WebsiteFakeProtector : INodeSecretProtector
{
    public ProtectedSecret Protect(string apiKey) => new("cipher", "hint");

    public string Unprotect(string cipherText) => "test-api-key";
}

internal static class WebsiteTestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>与真实 PanelClient 的 JsonContent.Create（Web 默认 camelCase）保持一致，便于断言请求体键名。</summary>
    public static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}

/// <summary>网站管理单元测试数据构造。</summary>
internal static class WebsiteTestData
{
    public static NodeRecord Node(string name = "node-a", bool enabled = true) => new(
        NodeId.New(),
        name,
        new Uri("https://" + name + ".example.test/"),
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
        enabled,
        "v2.3.2",
        0,
        NodeHealth.Online,
        null,
        null,
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    public static OperationPlanContext PlanContext(
        NodeRecord[] nodes,
        System.Text.Json.Nodes.JsonObject? parameters,
        string kind = "website.start") =>
        new(
            new OperationInvocation(kind, nodes.Select(node => node.Id).ToArray(), parameters),
            nodes,
            OperationParameters.From(parameters));

    public static OperationNodeContext NodeContext(
        NodeRecord node,
        System.Text.Json.Nodes.JsonObject? parameters,
        IPanelClient client) =>
        new(Guid.NewGuid(), node, client, OperationParameters.From(parameters));

    public static NodeDescriptor Descriptor(NodeRecord node) => new(
        node.Id,
        node.Name,
        node.BaseUrl,
        "test-api-key",
        node.AuthMode,
        node.TlsPolicy);

    public static System.Text.Json.Nodes.JsonObject TargetParameters(
        long websiteId = 3,
        string name = "example.com",
        params (string Key, object? Value)[] extras)
    {
        var parameters = new System.Text.Json.Nodes.JsonObject
        {
            ["websiteId"] = websiteId,
            ["name"] = name
        };

        foreach (var (key, value) in extras)
        {
            parameters[key] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value));
        }

        return parameters;
    }
}

/// <summary>网站管理测试用假响应 JSON。</summary>
internal static class WebsiteTestJsonData
{
    public const string Groups = """
        [{"id":1,"name":"Default","type":"website","isDefault":true},{"id":2,"name":"项目A","type":"website","isDefault":false}]
        """;

    public const string GroupOneWebsite = """
        {"total":1,"items":[{"id":11,"primaryDomain":"site-a.example.com","alias":"site-a","type":"static","status":"Running","protocol":"HTTPS","expireDate":"2027-01-01T00:00:00Z","sitePath":"/1panel/apps/openresty/www/sites/site-a","appName":"","runtimeName":"","favorite":true,"remark":"A 站","sslStatus":"ready","sslExpireDate":"2026-12-01T00:00:00Z"}]}
        """;

    public const string GroupTwoWebsite = """
        {"total":1,"items":[{"id":12,"primaryDomain":"site-b.example.com","alias":"site-b","type":"proxy","status":"Stopped","protocol":"HTTP","expireDate":"9999-12-31T00:00:00Z","sitePath":"/1panel/apps/openresty/www/sites/site-b","favorite":false,"remark":"B 站"}]}
        """;

    public const string Detail = """
        {"id":11,"createdAt":"2026-01-01T00:00:00Z","protocol":"HTTPS","primaryDomain":"site-a.example.com","type":"static","alias":"site-a","remark":"A 站","status":"Running","expireDate":"2027-01-01T00:00:00Z","sitePath":"/1panel/www/sites/site-a","siteDir":"/1panel/www/sites/site-a","appName":"","appInstallId":0,"runtimeID":2,"runtimeName":"PHP-8030","runtimeType":"php","webSiteGroupId":2,"favorite":true,"IPV6":true,"defaultServer":false,"openBaseDir":false,"accessLog":true,"errorLog":false,"accessLogPath":"/1panel/www/sites/site-a/log/access.log","errorLogPath":"/1panel/www/sites/site-a/log/error.log","domains":[{"domain":"site-a.example.com","port":80,"ssl":true},{"domain":"www.site-a.example.com","port":80,"ssl":false}],"childSites":[]}
        """;

    public const string Domains = """
        [{"id":21,"websiteId":11,"domain":"site-a.example.com","port":80,"ssl":true},{"id":22,"websiteId":11,"domain":"www.site-a.example.com","port":81,"ssl":false}]
        """;

    public const string Proxies = """
        [{"id":11,"operate":"","enable":true,"cache":false,"cacheTime":0,"cacheUnit":"","serverCacheTime":10,"serverCacheUnit":"m","name":"api","modifier":"","match":"/api","proxyPass":"http://127.0.0.1:8080","proxyHost":"$host","filePath":"/1panel/proxy/api.conf","sni":false,"proxySSLName":"","sslVerify":false,"cors":false,"allowOrigins":"","allowMethods":"","allowHeaders":"","allowCredentials":false,"preflight":false}]
        """;

    public const string Https = """
        {"enable":true,"httpConfig":"HTTPToHTTPS","SSL":{"id":2,"primaryDomain":"*.example.com","domains":"*.example.com","type":"ZeroSSL RSA DV SSL CA 2","provider":"manual","organization":"ZeroSSL GmbH","status":"ready","message":"","expireDate":"2026-12-26T23:59:59Z","startDate":"2026-09-27T00:00:00Z","autoRenew":false,"description":"","isIP":false,"keyType":"","certPath":"/1panel/ssl/cert.pem","privateKeyPath":"/1panel/ssl/key.pem","pushDir":false,"dir":"","privateKey":"-----BEGIN PRIVATE KEY-----secret","pem":"-----BEGIN CERTIFICATE-----secret"},"SSLProtocol":["TLSv1.2","TLSv1.3"],"algorithm":"","hsts":false,"hstsIncludeSubDomains":false,"httpsPort":"443","httpsPorts":[443],"http3":false}
        """;

    public const string SslList = """
        [{"id":2,"primaryDomain":"*.example.com","domains":"*.example.com","type":"ZeroSSL RSA DV SSL CA 2","provider":"manual","organization":"ZeroSSL GmbH","status":"ready","message":"","expireDate":"2026-12-26T23:59:59Z","startDate":"2026-09-27T00:00:00Z","autoRenew":false,"description":"","isIP":false,"keyType":"","certPath":"/1panel/ssl/cert.pem","privateKeyPath":"/1panel/ssl/key.pem","pushDir":false,"dir":"","logPath":"/1panel/log/ssl.log","privateKey":"-----BEGIN PRIVATE KEY-----secret","pem":"-----BEGIN CERTIFICATE-----secret"}]
        """;

    public const string Runtimes = """
        {"total":1,"items":[{"id":2,"name":"PHP-8030","resource":"appstore","appDetailID":544,"appID":219,"status":"Running","type":"php","image":"1panel-php-fpm:8.0.30","message":""}]}
        """;
}
