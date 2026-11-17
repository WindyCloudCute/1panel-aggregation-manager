using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Logs;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P10 系统日志契约测试：真实 <see cref="PanelClientFactory"/> + 真实 HTTP 打到假面板，
/// 验证面板/系统/网站日志路由、请求参数与响应映射；系统日志 raw 字段不进入 DTO。
/// </summary>
public sealed class LogContractTests
{
    [Fact]
    public async Task PanelOperation_ParsesPagedItems_AndSendsPaging()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [LogRoutes.PanelOperation] =
                    """
                    {"total":1,"items":[{"id":1,"source":"settings","user":"admin","ip":"198.51.100.1",
                    "path":"/core/settings/upgrade","method":"post","status":"Success","message":"",
                    "detailZH":"更新系统 => [v2.5.1]","latency":39353655,"createdAt":"2026-11-16T23:05:59Z"}]}
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var result = await client.PostAsync<PagedLogItemsDto<PanelOperationLogDto>>(
            LogRoutes.PanelOperation,
            new { page = 1, pageSize = 50 });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data?.Total);
        var item = Assert.Single(result.Data!.Items!);
        Assert.Equal("settings", item.Source);
        Assert.Equal("admin", item.User);
        Assert.Equal("Success", item.Status);
        Assert.Contains("更新系统", item.DetailZH, StringComparison.Ordinal);
        Assert.Equal(39353655, item.Latency);
        Assert.NotNull(item.CreatedAt);

        var body = server.GetLastRequestBody(LogRoutes.PanelOperation);
        Assert.NotNull(body);
        Assert.Contains("\"page\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PanelLogin_ParsesItems()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [LogRoutes.PanelLogin] =
                    """
                    {"total":1,"items":[{"id":2,"ip":"198.51.100.1","user":"admin","address":"Intranet",
                    "agent":"Mozilla/5.0","status":"Success","message":"","createdAt":"2026-11-16T12:12:28Z"}]}
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var result = await client.PostAsync<PagedLogItemsDto<PanelLoginLogDto>>(
            LogRoutes.PanelLogin,
            new { page = 1, pageSize = 20, info = (string?)null, startTime = (string?)null, endTime = (string?)null });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Data!.Items!);
        Assert.Equal("admin", item.User);
        Assert.Equal("Intranet", item.Address);
        Assert.Equal("Success", item.Status);
        Assert.NotNull(item.CreatedAt);
    }

    [Fact]
    public async Task SystemRead_ParsesItemsHasMore_AndDropsRawFromDto()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [LogRoutes.SystemRead] =
                    """
                    {"source":"journalctl","items":[{"time":"2026-11-16 23:59:35","priority":"6",
                    "service":"1panel-agent.service","message":"[GIN] GET /api/v2/websites/1",
                    "raw":"{\"SECRET\":\"must-not-be-mapped\"}"}],"hasMore":true,"nextCursor":"cursor-1"}
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var result = await client.PostAsync<SystemLogPageDto>(
            LogRoutes.SystemRead,
            new { pageSize = 100, cursor = (string?)null, startTime = (string?)null, endTime = (string?)null, keyword = "GIN", priority = (string?)null, service = (string?)null });

        Assert.True(result.IsSuccess);
        Assert.Equal("journalctl", result.Data?.Source);
        Assert.True(result.Data!.HasMore);
        Assert.Equal("cursor-1", result.Data.NextCursor);
        var item = Assert.Single(result.Data.Items!);
        Assert.Equal("6", item.Priority);
        Assert.Equal("1panel-agent.service", item.Service);
        Assert.Contains("GIN", item.Message, StringComparison.Ordinal);

        // 安全契约：系统日志 raw（journal 完整 JSON）不得映射到 DTO。
        Assert.Null(typeof(SystemLogItemDto).GetProperty("Raw"));

        var body = server.GetLastRequestBody(LogRoutes.SystemRead);
        Assert.NotNull(body);
        Assert.Contains("\"pageSize\":100", body, StringComparison.Ordinal);
        Assert.Contains("\"keyword\":\"GIN\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemStatusServicesAndFiles_Parse()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [LogRoutes.SystemStatus] =
                    """{"source":"journalctl","version":"systemd 257","keywordFilterSupported":true,"message":""}""",
                [LogRoutes.SystemServices] = """["1panel-agent.service","docker.service"]""",
                [LogRoutes.SystemFiles] = """["2026-11-16","2026-11-15"]"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var status = await client.GetAsync<SystemLogStatusDto>(LogRoutes.SystemStatus);
        Assert.True(status.IsSuccess);
        Assert.Equal("journalctl", status.Data?.Source);
        Assert.True(status.Data!.KeywordFilterSupported);

        var services = await client.GetAsync<string[]>(LogRoutes.SystemServices);
        Assert.True(services.IsSuccess);
        Assert.Equal(new[] { "1panel-agent.service", "docker.service" }, services.Data);

        var files = await client.GetAsync<string[]>(LogRoutes.SystemFiles);
        Assert.True(files.IsSuccess);
        Assert.Equal(new[] { "2026-11-16", "2026-11-15" }, files.Data);
    }

    [Fact]
    public async Task WebsiteFlow_ResolvesTargetThenReadsLog()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                // 网站列表复用 FakePanelServer 已内置的 P4 假数据（2 个网站，id=1 为 example.com）。
                [LogRoutes.WebsiteLog] =
                    """{"enable":true,"content":"127.0.0.1 - GET /a\n127.0.0.1 - GET /b","end":true,"path":"/logs/access.log"}"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var search = await client.PostAsync<WebsiteLogTargetPageDto>(
            LogRoutes.WebsiteSearch,
            new { page = 1, pageSize = 20, name = "example.com", orderBy = "created_at", order = "descending" });
        Assert.True(search.IsSuccess);
        var target = Assert.Single(search.Data!.Items!, item => item.PrimaryDomain == "example.com");
        Assert.Equal(1, target.Id);
        Assert.Equal("example.com", target.PrimaryDomain);

        var log = await client.PostAsync<WebsiteLogDto>(
            LogRoutes.WebsiteLog,
            new { id = target.Id, logType = "access.log", page = 1, pageSize = 100 });
        Assert.True(log.IsSuccess);
        Assert.True(log.Data!.Enable);
        Assert.True(log.Data.End);
        Assert.Contains("GET /a", log.Data.Content, StringComparison.Ordinal);
        Assert.Equal("/logs/access.log", log.Data.Path);

        var logBody = server.GetLastRequestBody(LogRoutes.WebsiteLog);
        Assert.NotNull(logBody);
        Assert.Contains("\"id\":1", logBody, StringComparison.Ordinal);
        Assert.Contains("\"logType\":\"access.log\"", logBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Routes_MatchProbedPanelApiRoutes()
    {
        Assert.Equal("api/v2/core/logs/operation", LogRoutes.PanelOperation);
        Assert.Equal("api/v2/core/logs/login", LogRoutes.PanelLogin);
        Assert.Equal("api/v2/logs/system/status", LogRoutes.SystemStatus);
        Assert.Equal("api/v2/logs/system/files", LogRoutes.SystemFiles);
        Assert.Equal("api/v2/logs/system/services", LogRoutes.SystemServices);
        Assert.Equal("api/v2/logs/system/read", LogRoutes.SystemRead);
        Assert.Equal("api/v2/websites/log/search", LogRoutes.WebsiteLog);
    }

    private static IPanelClient CreateClient(FakePanelServer server)
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(5) },
            TimeProvider.System,
            NullLoggerFactory.Instance);

        return factory.Create(new NodeDescriptor(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            "test-api-key",
            AuthMode.HmacSha256,
            TlsPolicy.Strict));
    }
}
