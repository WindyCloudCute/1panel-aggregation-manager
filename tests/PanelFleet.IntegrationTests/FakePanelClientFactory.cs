using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

public enum FakePanelBehavior
{
    Success = 0,
    Unauthorized = 1,
    Network = 2,
    Slow = 3,
    ServerError = 4
}

/// <summary>
/// 集成测试用 1Panel 客户端工厂：按节点名脚本化行为，按路径返回 P4 五类资源假数据，
/// 并统计每个节点的出站调用次数与请求路径。
/// </summary>
public sealed class FakePanelClientFactory : IPanelClientFactory
{
    private readonly ConcurrentDictionary<string, FakePanelBehavior> _behaviors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _pathCalls =
        new(StringComparer.OrdinalIgnoreCase);

    public void SetBehavior(string nodeName, FakePanelBehavior behavior) => _behaviors[nodeName] = behavior;

    public int Calls(string nodeName) => _calls.TryGetValue(nodeName, out var count) ? count : 0;

    public int TotalCalls => _calls.Values.Sum();

    /// <summary>某节点在路径包含 <paramref name="pathContains"/> 的调用次数（如 "websites/search"、"databases/db/list/"）。</summary>
    public int CallsForPath(string nodeName, string pathContains) =>
        _pathCalls.TryGetValue(nodeName, out var paths)
            ? paths
                .Where(path => path.Key.Contains(pathContains, StringComparison.OrdinalIgnoreCase))
                .Sum(path => path.Value)
            : 0;

    /// <summary>某节点被请求过的路径（去重，用于断言"每节点每类 1 次"之外的额外调用）。</summary>
    public IReadOnlyList<string> PathsCalled(string nodeName) =>
        _pathCalls.TryGetValue(nodeName, out var paths)
            ? paths.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    public IPanelClient Create(NodeDescriptor node) => new FakePanelClient(this, node);

    internal void RecordCall(string nodeName, string? relativePath = null)
    {
        _calls.AddOrUpdate(nodeName, 1, static (_, count) => count + 1);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var paths = _pathCalls.GetOrAdd(
            nodeName,
            static _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        paths.AddOrUpdate(relativePath, 1, static (_, count) => count + 1);
    }

    internal FakePanelBehavior BehaviorFor(string nodeName) =>
        _behaviors.TryGetValue(nodeName, out var behavior) ? behavior : FakePanelBehavior.Success;

    private sealed class FakePanelClient : IPanelClient
    {
        private readonly FakePanelClientFactory _factory;

        public FakePanelClient(FakePanelClientFactory factory, NodeDescriptor node)
        {
            _factory = factory;
            Node = node;
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            SendAsync<T>(relativePath, body: null, cancellationToken);

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            SendAsync<T>(relativePath, body, cancellationToken);

        public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<OsInfoDto>(
                null,
                new PanelError(PanelErrorKind.Unsupported, "fake"),
                TimeSpan.Zero));

        public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<DashboardCurrentDto>(
                null,
                new PanelError(PanelErrorKind.Unsupported, "fake"),
                TimeSpan.Zero));

        public async Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
            string ioOption = "all",
            string netOption = "all",
            CancellationToken cancellationToken = default)
        {
            _factory.RecordCall(Node.Name, PanelRoutes.DashboardBase(ioOption, netOption));
            var behavior = _factory.BehaviorFor(Node.Name);

            switch (behavior)
            {
                case FakePanelBehavior.Slow:
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    return Success();
                case FakePanelBehavior.Unauthorized:
                    return new PanelCallResult<DashboardBaseDto>(
                        null,
                        new PanelError(PanelErrorKind.Unauthorized, $"[{Node.Name}] API Key 错误"),
                        TimeSpan.FromMilliseconds(5));
                case FakePanelBehavior.Network:
                    return new PanelCallResult<DashboardBaseDto>(
                        null,
                        new PanelError(PanelErrorKind.Network, $"[{Node.Name}] 无法连接节点"),
                        TimeSpan.FromMilliseconds(5));
                case FakePanelBehavior.ServerError:
                    return new PanelCallResult<DashboardBaseDto>(
                        null,
                        new PanelError(PanelErrorKind.PanelServerError, $"[{Node.Name}] 1Panel 内部错误"),
                        TimeSpan.FromMilliseconds(5));
                default:
                    return Success();
            }
        }

        public void Dispose()
        {
        }

        private async Task<PanelCallResult<T>> SendAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            _factory.RecordCall(Node.Name, relativePath);
            var behavior = _factory.BehaviorFor(Node.Name);

            switch (behavior)
            {
                case FakePanelBehavior.Slow:
                    // 与现有 Overview 行为一致：不自行吞掉取消，交给 FanoutEngine 归类为 Timeout。
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    return Resolve<T>(relativePath, body, stopwatch.Elapsed);
                case FakePanelBehavior.Unauthorized:
                    return new PanelCallResult<T>(
                        default,
                        new PanelError(PanelErrorKind.Unauthorized, $"[{Node.Name}] API Key 错误"),
                        stopwatch.Elapsed);
                case FakePanelBehavior.Network:
                    return new PanelCallResult<T>(
                        default,
                        new PanelError(PanelErrorKind.Network, $"[{Node.Name}] 无法连接节点"),
                        stopwatch.Elapsed);
                case FakePanelBehavior.ServerError:
                    return new PanelCallResult<T>(
                        default,
                        new PanelError(PanelErrorKind.PanelServerError, $"[{Node.Name}] 1Panel 内部错误"),
                        stopwatch.Elapsed);
                default:
                    return Resolve<T>(relativePath, body, stopwatch.Elapsed);
            }
        }

        private PanelCallResult<T> Resolve<T>(string relativePath, object? body, TimeSpan elapsed)
        {
            var custom = FakeResourcePayloads.ResolveCustomJson(relativePath, body);
            return custom is null
                ? FakeResourcePayloads.CreateResult<T>(relativePath, Node.Name, elapsed)
                : FakeResourcePayloads.CreateResultFromJson<T>(custom, Node.Name, elapsed);
        }

        private PanelCallResult<DashboardBaseDto> Success() => new(
            new DashboardBaseDto
            {
                Hostname = "host-" + Node.Name,
                Os = "linux",
                Platform = "debian",
                PlatformVersion = "13.7",
                PrettyDistro = "Debian GNU/Linux 13",
                KernelVersion = "7.0.14-28-pve",
                KernelArch = "x86_64",
                IpV4Addr = "198.51.100.10",
                CpuCores = 8,
                CpuLogicalCores = 2,
                CpuModelName = "AMD Ryzen 7 2700X",
                WebsiteNumber = 3,
                AppInstalledNumber = 2,
                DatabaseNumber = 1,
                CronjobNumber = 4,
                CurrentInfo = new DashboardCurrentDto
                {
                    CpuUsedPercent = 12.5,
                    MemoryUsedPercent = 30,
                    MemoryUsed = 1509559296,
                    MemoryTotal = 2147483648,
                    MemoryAvailable = 637924352,
                    Load1 = 0.5,
                    Load5 = 0.6,
                    Load15 = 0.7,
                    Uptime = 488434,
                    ShotTime = DateTimeOffset.UtcNow,
                    DiskData =
                    [
                        new DiskInfoDto
                        {
                            Path = "/",
                            Type = "zfs",
                            Device = "rpool/data",
                            Total = 34359738368,
                            Used = 20727857152,
                            Free = 13631881216,
                            UsedPercent = 60.32
                        },
                        new DiskInfoDto
                        {
                            Path = "/data/Routing",
                            Type = "cifs",
                            Device = "//198.51.100.1/Data",
                            Total = 1967846068224,
                            Used = 1863630278656,
                            Free = 104215789568,
                            UsedPercent = 94.7
                        }
                    ]
                }
            },
            null,
            TimeSpan.FromMilliseconds(8));
    }
}

/// <summary>
/// 集成测试假客户端使用的 P4 五类资源报文（字段对齐 FakePanelServer / 1Panel v2.3.2 真实响应）。
/// </summary>
internal static class FakeResourcePayloads
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private static readonly string WebsiteListJson = PagedJson(2, WebsitesItemsJson);

    /// <summary>1Panel 自定义分组（groups/search）的 data 部分：网站 + 计划任务分组，用于树状视图分类。</summary>
    public const string GroupsJson = """
        [
          {"id":2,"name":"Default","type":"host","isDefault":true},
          {"id":1,"name":"Default","type":"website","isDefault":true},
          {"id":3,"name":"生产站点","type":"website","isDefault":false},
          {"id":5,"name":"Default","type":"cronjob","isDefault":true},
          {"id":6,"name":"运维任务","type":"cronjob","isDefault":false}
        ]
        """;

    private static readonly string AppListJson = "{\"total\":2,\"items\":" + AppsItemsJson + "}";

    private static readonly string ContainerListJson = "{\"total\":2,\"items\":" + ContainersItemsJson + "}";

    private static readonly string CronjobListJson = "{\"total\":1,\"items\":" + CronjobsItemsJson + "}";

    private static readonly string DatabaseListJson = "{\"total\":2,\"items\":" + MysqlDatabasesItemsJson + "}";

    private static readonly string EmptyDatabaseListJson = """{"total":0,"items":[]}""";

    private static string PagedJson(int total, string itemsJson) =>
        "{\"total\":" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"items\":" + itemsJson + "}";

    /// <summary>按路径/请求体返回自定义假报文（分组、按分组过滤网站）；返回 null 时走默认路径解析。</summary>
    public static string? ResolveCustomJson(string relativePath, object? body)
    {
        var path = relativePath.TrimStart('/');

        if (path.Contains("groups/search", StringComparison.OrdinalIgnoreCase))
        {
            return GroupsJson;
        }

        if (path.Contains("websites/search", StringComparison.OrdinalIgnoreCase))
        {
            // 网站列表接口不返回分组字段，loader 按 websiteGroupId 分组查询：1=默认分组，3=生产站点。
            return ReadLong(body, "websiteGroupId") switch
            {
                1 => PagedJson(1, "[" + Website1Json + "]"),
                3 => PagedJson(1, "[" + Website2Json + "]"),
                _ => WebsiteListJson
            };
        }

        return null;
    }

    public static PanelCallResult<T> CreateResultFromJson<T>(string json, string nodeName, TimeSpan elapsed)
    {
        try
        {
            return new PanelCallResult<T>(JsonSerializer.Deserialize<T>(json, JsonOptions), null, elapsed);
        }
        catch (JsonException exception)
        {
            return new PanelCallResult<T>(
                default,
                new PanelError(
                    PanelErrorKind.Protocol,
                    $"[{nodeName}] 假数据 JSON 解析失败：{exception.Message}",
                    Cause: exception),
                elapsed);
        }
    }

    private static long? ReadLong(object? body, string propertyName)
    {
        if (body is null)
        {
            return null;
        }

        var value = body.GetType().GetProperty(propertyName)?.GetValue(body);
        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            _ => null
        };
    }

    public static PanelCallResult<T> CreateResult<T>(string relativePath, string nodeName, TimeSpan elapsed)
    {
        var path = relativePath.TrimStart('/');

        try
        {
            if (TryResolve(path, out var json))
            {
                return new PanelCallResult<T>(
                    JsonSerializer.Deserialize<T>(json, JsonOptions),
                    null,
                    elapsed);
            }

            if (IsNullDataPath(path))
            {
                // 1Panel 未安装该引擎时 data 为 null，成功但无数据。
                return new PanelCallResult<T>(default, null, elapsed);
            }

            return new PanelCallResult<T>(
                default,
                new PanelError(PanelErrorKind.NotFound, $"[{nodeName}] 假客户端未实现路径：{relativePath}"),
                elapsed);
        }
        catch (JsonException exception)
        {
            return new PanelCallResult<T>(
                default,
                new PanelError(
                    PanelErrorKind.Protocol,
                    $"[{nodeName}] 假数据 JSON 解析失败：{exception.Message}",
                    Cause: exception),
                elapsed);
        }
    }

    private static bool IsNullDataPath(string path) =>
        path.Contains("databases/db/list/", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("databases/db/list/mysql", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("databases/db/list/mariadb", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolve(string path, out string json)
    {
        if (path.Contains("websites/search", StringComparison.OrdinalIgnoreCase))
        {
            json = WebsiteListJson;
            return true;
        }

        if (path.Contains("apps/installed/search", StringComparison.OrdinalIgnoreCase))
        {
            json = AppListJson;
            return true;
        }

        if (path.Contains("containers/search", StringComparison.OrdinalIgnoreCase))
        {
            json = ContainerListJson;
            return true;
        }

        if (path.Contains("cronjobs/search", StringComparison.OrdinalIgnoreCase))
        {
            json = CronjobListJson;
            return true;
        }

        if (path.Contains("databases/db/list/mysql", StringComparison.OrdinalIgnoreCase)
            || path.Contains("databases/db/list/mariadb", StringComparison.OrdinalIgnoreCase))
        {
            json = MysqlInstancesJson;
            return true;
        }

        if (path.Contains("databases/pg/search", StringComparison.OrdinalIgnoreCase)
            || path.Contains("databases/mongodb/search", StringComparison.OrdinalIgnoreCase))
        {
            json = EmptyDatabaseListJson;
            return true;
        }

        if (path.Contains("databases/search", StringComparison.OrdinalIgnoreCase))
        {
            json = DatabaseListJson;
            return true;
        }

        if (path.Contains("websites/", StringComparison.OrdinalIgnoreCase))
        {
            json = WebsiteDetailJson;
            return true;
        }

        if (path.Contains("apps/installed/info/", StringComparison.OrdinalIgnoreCase))
        {
            json = AppDetailJson;
            return true;
        }

        if (path.Contains("containers/info", StringComparison.OrdinalIgnoreCase))
        {
            json = ContainerInfoJson;
            return true;
        }

        if (path.Contains("cronjobs/load/info", StringComparison.OrdinalIgnoreCase))
        {
            json = CronjobInfoJson;
            return true;
        }

        json = string.Empty;
        return false;
    }

    private const string Website1Json = """
        {"id":1,"createdAt":"2026-11-10T14:20:12+08:00","protocol":"HTTPS","primaryDomain":"example.com","type":"static","alias":"example.com","remark":"公司官网","status":"Running","expireDate":"9999-12-31T00:00:00+08:00","sitePath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com","appName":"","runtimeName":"","runtimeType":"","sslExpireDate":"2027-01-01T00:00:00+08:00","sslStatus":"ready","childSites":[],"parentSite":"","favorite":true,"IPV6":true}
        """;

    private const string Website2Json = """
        {"id":2,"createdAt":"2026-11-11T09:05:00+08:00","protocol":"HTTP","primaryDomain":"blog.example.com","type":"proxy","alias":"blog","remark":"","status":"Stopped","expireDate":"9999-12-31T00:00:00+08:00","sitePath":"/opt/1panel/apps/openresty/openresty/www/sites/blog.example.com","appName":"wordpress","runtimeName":"","runtimeType":"","sslExpireDate":"0001-01-01T00:00:00Z","sslStatus":"init","childSites":["news.example.com"],"parentSite":"","favorite":false,"IPV6":false}
        """;

    private const string WebsitesItemsJson = "[" + Website1Json + "," + Website2Json + "]";

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
          {"id":1,"name":"备份网站数据","type":"backupWebsite","groupID":6,"spec":"0 3 * * *","specs":["0","3","*","*","*"],"executor":"shell","scriptMode":"","script":"","command":"tar -czf /backup/site.tar.gz /www/sites/example.com","containerName":"","user":"","appID":"","website":"example.com","dbType":"","dbName":"","url":"","retainCopies":7,"retryTimes":3,"timeout":3600,"ignoreErr":false,"status":"Enable","lastRecordStatus":"Success","lastRecordTime":"2026-11-16 03:00:05","alertCount":0}
        ]
        """;

    private const string WebsiteDetailJson = """
        {"id":1,"createdAt":"2026-11-10T14:20:12+08:00","updatedAt":"2026-11-16T09:00:00+08:00","protocol":"HTTPS","primaryDomain":"example.com","type":"static","alias":"example.com","remark":"公司官网","status":"Running","expireDate":"9999-12-31T00:00:00+08:00","sitePath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com","siteDir":"/opt/1panel/apps/openresty/openresty/www/sites/example.com","appName":"","runtimeName":"","runtimeType":"","errorLogPath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com/log/error.log","accessLogPath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com/log/access.log","domains":["example.com","www.example.com"],"IPV6":true,"favorite":true,"webSiteGroupId":0,"dbType":"","dbID":0,"streamPorts":[]}
        """;

    private const string AppDetailJson = """
        {"id":1,"name":"mysql","version":"8.4.6","status":"Running","message":"","HttpPort":3306,"container":"1Panel-mysql-a1b2c3","composePath":"/opt/1panel/apps/mysql/mysql/docker-compose.yml","appKey":"mysql","appPorts":[3306],"env":{"MYSQL_PORT":"3306"}}
        """;

    private const string ContainerInfoJson = """
        {"name":"mysql","image":"mysql:8.4.6","hostname":"mysql","domainName":"","dns":[],"networks":[{"name":"bridge","ip":"172.17.0.2"}],"ports":[{"hostPort":"3306","containerPort":"3306","protocol":"tcp"}],"volumes":[],"env":["MYSQL_DATABASE=wordpress"],"labels":[],"restartPolicy":"always","workingDir":"","cmd":"","entrypoint":"","cpuShares":0,"memory":0}
        """;

    private const string CronjobInfoJson = """
        {"id":1,"name":"备份网站数据","type":"backupWebsite","groupID":0,"spec":"0 3 * * *","executor":"shell","scriptMode":"","script":"","command":"tar -czf /backup/site.tar.gz /www/sites/example.com","website":"example.com","retainCopies":7,"retryTimes":3,"timeout":3600,"ignoreErr":false,"status":"Enable"}
        """;
}
