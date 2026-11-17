using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PanelFleet.Client.ContractTests.FakePanel;

/// <summary>
/// 进程内 Kestrel 假 1Panel 服务，用于契约测试。
/// 除 P1/P3 的 dashboard/settings 路由外，还提供 P4 五类只读资源的列表与详情假数据；
/// 所有 POST 请求体按路径记录在 <see cref="LastRequestBodies"/>，供契约测试断言客户端发出的参数。
/// </summary>
public sealed class FakePanelServer : IAsyncDisposable
{
    private readonly FakePanelOptions _options;
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, string> _lastRequestBodies = new(StringComparer.Ordinal);
    private int _requestCount;

    public FakePanelServer(FakePanelOptions options)
    {
        _options = options;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.Use(ValidateSignatureAsync);
        _app.Use(RecordRequestBodyAsync);

        _app.MapGet(
            "/api/v2/dashboard/base/os",
            (HttpContext context) => RespondAsync(
                context,
                """{"code":200,"data":{"os":"Ubuntu","platform":"linux","kernelVersion":"6.8"}}"""));

        _app.MapGet(
            "/api/v2/dashboard/current/node",
            (HttpContext context) => RespondAsync(
                context,
                """{"code":200,"data":{"cpuUsedPercent":12.5,"memoryUsedPercent":33.3}}"""));

        _app.MapPost(
            "/api/v2/settings/search",
            (HttpContext context) => RespondAsync(
                context,
                """{"code":200,"data":{"systemVersion":"v2.0.15","defaultIO":"all","defaultNetwork":"all"}}"""));

        _app.MapGet(
            "/api/v2/dashboard/base/{io}/{net}",
            (HttpContext context, string io, string net) => RespondAsync(context, DashboardBaseJson()));

        MapResourceRoutes();
        MapExtraRoutes();
    }

    public Uri BaseUri { get; private set; } = null!;

    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>每个请求路径最后收到的非空请求体（键为去掉前导斜杠的路径，如 <c>api/v2/websites/search</c>）。</summary>
    public ConcurrentDictionary<string, string> LastRequestBodies => _lastRequestBodies;

    /// <summary>读取某个路径最后收到的请求体；路径可带或不带前导斜杠。未收到时返回 <c>null</c>。</summary>
    public string? GetLastRequestBody(string path) =>
        _lastRequestBodies.TryGetValue(NormalizePath(path), out var body) ? body : null;

    public async Task<Uri> StartAsync()
    {
        await _app.StartAsync();
        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("假面板 Kestrel 未启动。");
        BaseUri = new Uri(address);
        return BaseUri;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
        }
        catch (InvalidOperationException)
        {
            // 未启动或已停止时忽略。
        }

        await _app.DisposeAsync();
    }

    private void MapResourceRoutes()
    {
        // ---- 网站 ----
        _app.MapPost(
            "/api/v2/websites/search",
            async (HttpContext context) =>
            {
                if (!await ValidateRequiredAsync(context, "page", "pageSize", "orderBy", "order").ConfigureAwait(false))
                {
                    return;
                }

                await RespondAsync(context, PagedJson(total: 2, WebsitesItemsJson)).ConfigureAwait(false);
            });

        _app.MapGet(
            "/api/v2/websites/{id}",
            async (HttpContext context, string id) =>
            {
                if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var websiteId))
                {
                    await RespondNotFoundAsync(context, "网站 id 非法。").ConfigureAwait(false);
                    return;
                }

                await RespondAsync(context, WebsiteDetailJson(websiteId)).ConfigureAwait(false);
            });

        // ---- 应用（已安装） ----
        _app.MapPost(
            "/api/v2/apps/installed/search",
            async (HttpContext context) =>
            {
                if (!await ValidateRequiredAsync(context, "page", "pageSize").ConfigureAwait(false))
                {
                    return;
                }

                await RespondAsync(context, PagedJson(total: 2, AppsItemsJson)).ConfigureAwait(false);
            });

        _app.MapGet(
            "/api/v2/apps/installed/info/{id}",
            async (HttpContext context, string id) =>
            {
                if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId))
                {
                    await RespondNotFoundAsync(context, "应用 id 非法。").ConfigureAwait(false);
                    return;
                }

                await RespondAsync(context, AppDetailJson(appId)).ConfigureAwait(false);
            });

        // ---- 容器 ----
        _app.MapPost(
            "/api/v2/containers/search",
            async (HttpContext context) =>
            {
                if (!await ValidateRequiredAsync(context, "page", "pageSize", "state", "orderBy", "order").ConfigureAwait(false))
                {
                    return;
                }

                await RespondAsync(context, PagedJson(total: 2, ContainersItemsJson)).ConfigureAwait(false);
            });

        _app.MapPost(
            "/api/v2/containers/info",
            async (HttpContext context) =>
            {
                var body = await TryReadBodyAsync(context).ConfigureAwait(false);
                var name = body is { ValueKind: JsonValueKind.Object } element
                    && element.TryGetProperty("name", out var nameProperty)
                    && nameProperty.ValueKind == JsonValueKind.String
                    ? nameProperty.GetString() ?? "mysql"
                    : "mysql";
                await RespondAsync(context, ContainerInfoJson(name)).ConfigureAwait(false);
            });

        // ---- 数据库：实例列表 + 库列表 ----
        _app.MapGet(
            "/api/v2/databases/db/list/{engine}",
            async (HttpContext context, string engine) =>
            {
                var json = engine.ToLowerInvariant() switch
                {
                    "mysql" or "mariadb" => DataEnvelope(MysqlInstancesJson),
                    // 真实节点未安装 PG/Mongo 时返回 data:null（不是空数组）。
                    "postgresql" or "mongodb" => DataEnvelope("null"),
                    _ => DataEnvelope("null")
                };
                await RespondAsync(context, json).ConfigureAwait(false);
            });

        _app.MapPost(
            "/api/v2/databases/search",
            async (HttpContext context) =>
            {
                if (!await ValidateRequiredAsync(context, "page", "pageSize", "database", "orderBy", "order")
                        .ConfigureAwait(false))
                {
                    return;
                }

                await RespondAsync(context, PagedJson(total: 2, MysqlDatabasesItemsJson)).ConfigureAwait(false);
            });

        _app.MapPost(
            "/api/v2/databases/pg/search",
            async (HttpContext context) =>
            {
                if (!await ValidateRequiredAsync(context, "page", "pageSize", "database", "orderBy", "order")
                        .ConfigureAwait(false))
                {
                    return;
                }

                await RespondAsync(context, PagedJson(total: 0, "[]")).ConfigureAwait(false);
            });

        _app.MapPost(
            "/api/v2/databases/mongodb/search",
            async (HttpContext context) =>
            {
                if (!await ValidateRequiredAsync(context, "page", "pageSize", "database", "orderBy", "order")
                        .ConfigureAwait(false))
                {
                    return;
                }

                await RespondAsync(context, PagedJson(total: 0, "[]")).ConfigureAwait(false);
            });

        // ---- 计划任务 ----
        _app.MapPost(
            "/api/v2/cronjobs/search",
            async (HttpContext context) =>
            {
                if (!await ValidateRequiredAsync(context, "page", "pageSize", "orderBy", "order").ConfigureAwait(false))
                {
                    return;
                }

                await RespondAsync(context, PagedJson(total: 1, CronjobsItemsJson)).ConfigureAwait(false);
            });

        _app.MapPost(
            "/api/v2/cronjobs/load/info",
            async (HttpContext context) =>
            {
                var body = await TryReadBodyAsync(context).ConfigureAwait(false);
                var id = body is { ValueKind: JsonValueKind.Object } element
                    && element.TryGetProperty("id", out var idProperty)
                    && idProperty.TryGetInt64(out var parsedId)
                    ? parsedId
                    : 1L;
                await RespondAsync(context, CronjobInfoJson(id)).ConfigureAwait(false);
            });
    }

    /// <summary>各阶段契约测试通过 <see cref="FakePanelOptions.JsonRoutes"/> / <see cref="FakePanelOptions.RawJsonRoutes"/> 注入的额外路由。</summary>
    private void MapExtraRoutes()
    {
        var methods = new[] { HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete };

        foreach (var (path, dataJson) in _options.JsonRoutes)
        {
            var body = dataJson is null
                ? """{"code":200,"message":"","data":null}"""
                : JsonSerializer.Serialize(new
                {
                    code = 200,
                    message = string.Empty,
                    data = JsonSerializer.Deserialize<JsonElement>(dataJson)
                });
            _app.MapMethods("/" + path.TrimStart('/'), methods, (HttpContext context) => RespondAsync(context, body));
        }

        foreach (var (path, rawJson) in _options.RawJsonRoutes)
        {
            _app.MapMethods("/" + path.TrimStart('/'), methods, (HttpContext context) => WriteJsonAsync(context, rawJson));
        }
    }

    private async Task ValidateSignatureAsync(HttpContext context, Func<Task> next)
    {
        Interlocked.Increment(ref _requestCount);

        if (_options.Redirect)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Redirect;
            context.Response.Headers.Location = "/login";
            return;
        }

        if (_options.Delay > TimeSpan.Zero)
        {
            await Task.Delay(_options.Delay, context.RequestAborted);
        }

        if (_options.RejectAll || (_options.RejectFirstRequest && RequestCount == 1))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            if (_options.RejectFirstRequest)
            {
                context.Response.Headers["Date"] = DateTimeOffset.UtcNow
                    .AddMinutes(5)
                    .ToString("R", CultureInfo.InvariantCulture);
            }

            return;
        }

        if (!IsSignatureValid(context.Request))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        await next();
    }

    /// <summary>记录 POST/PUT/PATCH 的请求体，供契约测试断言客户端发出的必填参数。</summary>
    private async Task RecordRequestBodyAsync(HttpContext context, Func<Task> next)
    {
        var method = context.Request.Method;
        if ((HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method))
            && context.Request.ContentLength is not 0)
        {
            context.Request.EnableBuffering();
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(
                context.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var requestBody = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            context.Request.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(requestBody))
            {
                _lastRequestBodies[NormalizePath(context.Request.Path.Value)] = requestBody;
            }
        }

        await next().ConfigureAwait(false);
    }

    private bool IsSignatureValid(HttpRequest request)
    {
        var timestampValue = request.Headers["1Panel-Timestamp"].ToString();
        if (!long.TryParse(timestampValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            return false;
        }

        var token = request.Headers["1Panel-Token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var timestampText = timestamp.ToString(CultureInfo.InvariantCulture);

        if (_options.AcceptHmac)
        {
            var expected = Convert.ToHexStringLower(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(_options.ApiKey),
                    Encoding.UTF8.GetBytes("1panel:" + timestampText)));
            if (string.Equals(token, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (_options.AcceptMd5)
        {
            var expected = Convert.ToHexStringLower(
                MD5.HashData(Encoding.UTF8.GetBytes("1panel" + _options.ApiKey + timestampText)));
            if (string.Equals(token, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private string DashboardBaseJson() => _options.OmitCurrentInfo
        ? """{"code":200,"data":{"hostname":"node-1","os":"linux","platform":"debian","platformVersion":"13.7","prettyDistro":"Debian GNU/Linux 13","kernelVersion":"7.0.14-28-pve","kernelArch":"x86_64","ipV4Addr":"198.51.100.10","cpuCores":8,"cpuLogicalCores":2,"websiteNumber":3,"appInstalledNumber":2,"databaseNumber":1,"cronjobNumber":4}}"""
        : """{"code":200,"data":{"hostname":"node-1","os":"linux","platform":"debian","platformVersion":"13.7","prettyDistro":"Debian GNU/Linux 13","kernelVersion":"7.0.14-28-pve","kernelArch":"x86_64","ipV4Addr":"198.51.100.10","cpuCores":8,"cpuLogicalCores":2,"cpuModelName":"AMD Ryzen","websiteNumber":3,"appInstalledNumber":2,"databaseNumber":1,"cronjobNumber":4,"currentInfo":{"uptime":488434,"load1":0.72,"load5":1.47,"load15":1.62,"cpuUsedPercent":2.23,"memoryTotal":2147483648,"memoryUsed":1509559296,"memoryUsedPercent":70.29,"memoryAvailable":637924352,"diskData":[{"path":"/","type":"zfs","device":"rpool/data","total":34359738368,"free":13631881216,"used":20727857152,"usedPercent":60.32},{"path":"/data/Routing","type":"cifs","device":"//198.51.100.1/Data","total":1967846068224,"free":104215789568,"used":1863630278656,"usedPercent":94.7}],"shotTime":"2026-11-16T06:00:21.486069412Z"}}}""";

    /// <summary>分页列表信封；<see cref="FakePanelOptions.OmitResourceItems"/> 为 true 时返回 <c>items:null</c>。</summary>
    private string PagedJson(int total, string itemsJson) => _options.OmitResourceItems
        ? string.Concat(
            """{"code":200,"message":"","data":{"total":""",
            total.ToString(CultureInfo.InvariantCulture),
            ""","items":null}}""")
        : string.Concat(
            """{"code":200,"message":"","data":{"total":""",
            total.ToString(CultureInfo.InvariantCulture),
            ""","items":""",
            itemsJson,
            "}}");

    private static string DataEnvelope(string dataJson) => string.Concat(
        """{"code":200,"message":"","data":""",
        dataJson,
        "}");

    private static string NormalizePath(string? path) => (path ?? string.Empty).TrimStart('/');

    private static async Task<JsonElement?> TryReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is 0)
        {
            return null;
        }

        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;
        try
        {
            using var document = await JsonDocument
                .ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
            context.Request.Body.Position = 0;
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            context.Request.Body.Position = 0;
            return null;
        }
    }

    /// <summary>校验请求体包含所有必填字段；缺失时写 HTTP 200 + body code=400 并返回 false（与 1Panel 行为一致）。</summary>
    private async Task<bool> ValidateRequiredAsync(HttpContext context, params string[] requiredFields)
    {
        var body = await TryReadBodyAsync(context).ConfigureAwait(false);
        JsonElement? root = body is { ValueKind: JsonValueKind.Object } element ? element : null;
        var missing = new List<string>();

        foreach (var field in requiredFields)
        {
            if (root is not { } objectBody
                || !objectBody.TryGetProperty(field, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                missing.Add(field);
            }
        }

        if (missing.Count == 0)
        {
            return true;
        }

        await RespondValidationAsync(context, $"参数错误: {string.Join(", ", missing)} 为必填").ConfigureAwait(false);
        return false;
    }

    private async Task RespondAsync(HttpContext context, string json)
    {
        if (_options.BusinessErrorCode is { } businessCode)
        {
            json = $$"""{"code":{{businessCode}},"message":"boom"}""";
        }
        else if (_options.MalformedJson)
        {
            json = "not-json";
        }
        else if (_options.ReturnHtml)
        {
            json = "<html><body>Access Temporarily Unavailable</body></html>";
            await WriteJsonAsync(context, json, "text/html").ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context, json).ConfigureAwait(false);
    }

    private async Task RespondValidationAsync(HttpContext context, string message)
    {
        var json = JsonSerializer.Serialize(new { code = 400, message, data = new { } });
        await WriteJsonAsync(context, json).ConfigureAwait(false);
    }

    private async Task RespondNotFoundAsync(HttpContext context, string message)
    {
        var json = JsonSerializer.Serialize(new { code = 404, message, data = new { } });
        await WriteJsonAsync(context, json).ConfigureAwait(false);
    }

    private async Task WriteJsonAsync(HttpContext context, string json, string contentType = "application/json")
    {
        var payload = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = contentType;

        if (_options.UseGzip)
        {
            using var output = new MemoryStream();
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                await gzip.WriteAsync(payload, context.RequestAborted);
            }

            context.Response.Headers["Content-Encoding"] = "gzip";
            await context.Response.Body.WriteAsync(output.ToArray(), context.RequestAborted);
            return;
        }

        await context.Response.Body.WriteAsync(payload, context.RequestAborted);
    }

    // ---- P4 五类资源假数据（字段对齐 1Panel v2.3.2 真实报文） ----

    private const string WebsitesItemsJson = """
        [
          {"id":1,"createdAt":"2026-11-10T14:20:12+08:00","protocol":"HTTPS","primaryDomain":"example.com","type":"static","alias":"example.com","remark":"公司官网","status":"Running","expireDate":"9999-12-31T00:00:00+08:00","sitePath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com","appName":"","runtimeName":"","runtimeType":"","sslExpireDate":"2027-01-01T00:00:00+08:00","sslStatus":"ready","childSites":[],"parentSite":"","favorite":true,"IPV6":true},
          {"id":2,"createdAt":"2026-11-11T09:05:00+08:00","protocol":"HTTP","primaryDomain":"blog.example.com","type":"proxy","alias":"blog","remark":"","status":"Stopped","expireDate":"9999-12-31T00:00:00+08:00","sitePath":"/opt/1panel/apps/openresty/openresty/www/sites/blog.example.com","appName":"wordpress","runtimeName":"","runtimeType":"","sslExpireDate":"0001-01-01T00:00:00Z","sslStatus":"init","childSites":["news.example.com"],"parentSite":"","favorite":false,"IPV6":false}
        ]
        """;

    private const string AppsItemsJson = """
        [
          {"id":1,"createdAt":"2026-11-10T10:00:00+08:00","name":"mysql","appID":37,"appDetailID":92,"version":"8.4.6","status":"Running","message":"","httpPort":3306,"httpsPort":0,"path":"/opt/1panel/apps/mysql/mysql","appKey":"mysql","appType":"database","appName":"MySQL","canUpdate":false,"icon":"","ready":1,"total":1,"favorite":true,"sortOrder":0},
          {"id":2,"createdAt":"2026-11-11T11:30:00+08:00","name":"uptime-kuma","appID":59,"appDetailID":1117,"version":"1.23.17","status":"Running","message":"1Panel-uptime-kuma-p32P 容器不存在","httpPort":9010,"httpsPort":0,"path":"/opt/1panel/apps/uptime-kuma/uptime-kuma","appKey":"uptime-kuma","appType":"website","appName":"Uptime Kuma","canUpdate":true,"icon":"","ready":0,"total":1,"favorite":false,"sortOrder":1}
        ]
        """;

    private const string ContainersItemsJson = """
        [
          {"containerID":"a1b2c3d4e5f6a1b2c3d4e5f6","name":"mysql","imageID":"sha256-a1b2c3","imageName":"mysql:8.4.6","createTime":"2026-11-10 10:01:22","state":"running","runTime":"Up 3 hours","network":["172.17.0.2"],"ports":["0.0.0.0:3306->3306/tcp"],"isFromApp":true,"isFromCompose":false,"appName":"mysql","appInstallName":"mysql","websites":[],"isPinned":false,"description":""},
          {"containerID":"f6e5d4c3b2a1f6e5d4c3b2a1","name":"uptime-kuma","imageID":"sha256-f6e5d4","imageName":"louislam/uptime-kuma:1.23.17","createTime":"2026-11-11 11:31:05","state":"exited","runTime":"Exited (0) 2 days ago","network":["172.17.0.3"],"ports":["0.0.0.0:9010->3001/tcp"],"isFromApp":false,"isFromCompose":true,"appName":"","appInstallName":"","websites":["example.com"],"isPinned":true,"description":"监控面板"}
        ]
        """;

    private const string MysqlInstancesJson = """
        [{"id":1,"type":"mysql","from":"remote","database":"MySQL","version":"8.x","address":"198.51.100.11"}]
        """;

    private const string MysqlDatabasesItemsJson = """
        [
          {"id":1,"createdAt":"2026-11-10T10:10:00+08:00","name":"wordpress","mysqlName":"MySQL","from":"remote","format":"utf8mb4","collation":"utf8mb4_0900_ai_ci","isDelete":false,"description":""},
          {"id":2,"createdAt":"2026-11-11T12:00:00+08:00","name":"panel_fleet","mysqlName":"MySQL","from":"remote","format":"utf8mb4","collation":"utf8mb4_general_ci","isDelete":false,"description":"PanelFleet 业务库"}
        ]
        """;

    private const string CronjobsItemsJson = """
        [
          {"id":1,"name":"备份网站数据","type":"backupWebsite","groupID":0,"spec":"0 3 * * *","specs":["0","3","*","*","*"],"executor":"shell","scriptMode":"","script":"","command":"tar -czf /backup/site.tar.gz /www/sites/example.com","containerName":"","user":"","appID":"","website":"example.com","dbType":"","dbName":"","url":"","retainCopies":7,"retryTimes":3,"timeout":3600,"ignoreErr":false,"status":"Enable","lastRecordStatus":"Success","lastRecordTime":"2026-11-16 03:00:05","alertCount":0}
        ]
        """;

    private static string WebsiteDetailJson(long id) =>
        WebsiteDetailTemplate.Replace("__ID__", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string AppDetailJson(long id) =>
        AppDetailTemplate.Replace("__ID__", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string ContainerInfoJson(string name) =>
        ContainerInfoTemplate.Replace("__NAME__", JsonSerializer.Serialize(name), StringComparison.Ordinal);

    private static string CronjobInfoJson(long id) =>
        CronjobInfoTemplate.Replace("__ID__", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private const string WebsiteDetailTemplate = """
        {"code":200,"message":"","data":{"id":__ID__,"createdAt":"2026-11-10T14:20:12+08:00","updatedAt":"2026-11-16T09:00:00+08:00","protocol":"HTTPS","primaryDomain":"example.com","type":"static","alias":"example.com","remark":"公司官网","status":"Running","expireDate":"9999-12-31T00:00:00+08:00","sitePath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com","siteDir":"/opt/1panel/apps/openresty/openresty/www/sites/example.com","appName":"","runtimeName":"","runtimeType":"","errorLogPath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com/log/error.log","accessLogPath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com/log/access.log","domains":["example.com","www.example.com"],"IPV6":true,"favorite":true,"webSiteGroupId":0,"dbType":"","dbID":0,"streamPorts":[]}}
        """;

    private const string AppDetailTemplate = """
        {"code":200,"message":"","data":{"id":__ID__,"name":"mysql","version":"8.4.6","status":"Running","message":"","HttpPort":3306,"container":"1Panel-mysql-a1b2c3","composePath":"/opt/1panel/apps/mysql/mysql/docker-compose.yml","appKey":"mysql","appPorts":[3306],"env":{"MYSQL_PORT":"3306"}}}
        """;

    private const string ContainerInfoTemplate = """
        {"code":200,"message":"","data":{"name":__NAME__,"image":"mysql:8.4.6","hostname":"mysql","domainName":"","dns":[],"networks":[{"name":"bridge","ip":"172.17.0.2"}],"ports":[{"hostPort":"3306","containerPort":"3306","protocol":"tcp"}],"volumes":[],"env":["MYSQL_DATABASE=wordpress"],"labels":[],"restartPolicy":"always","workingDir":"","cmd":"","entrypoint":"","cpuShares":0,"memory":0}}
        """;

    private const string CronjobInfoTemplate = """
        {"code":200,"message":"","data":{"id":__ID__,"name":"备份网站数据","type":"backupWebsite","groupID":0,"spec":"0 3 * * *","executor":"shell","scriptMode":"","script":"","command":"tar -czf /backup/site.tar.gz /www/sites/example.com","website":"example.com","retainCopies":7,"retryTimes":3,"timeout":3600,"ignoreErr":false,"status":"Enable"}}
        """;
}
