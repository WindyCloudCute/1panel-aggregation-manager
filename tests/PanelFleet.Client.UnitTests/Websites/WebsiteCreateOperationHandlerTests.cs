using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Application.Websites;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Websites;

/// <summary>WEBSITE+ 新增写操作处理器单元测试：建站、设主域名、申请证书、关闭 HTTPS。</summary>
public sealed class WebsiteCreateOperationHandlerTests
{
    private readonly WebsiteCreateHandler _create = new();
    private readonly DomainPrimaryHandler _domainPrimary = new();
    private readonly SslRequestHandler _sslRequest = new();
    private readonly SslDisableHandler _sslDisable = new();

    private static JsonObject CreateParameters(string type = "static") => new()
    {
        ["type"] = type,
        ["primaryDomain"] = "new.example.com",
        ["otherDomains"] = new JsonArray("www.new.example.com"),
        ["alias"] = "新站",
        ["remark"] = "测试建站",
        ["webSiteGroupId"] = 2,
        ["port"] = 8080
    };

    [Fact]
    public async Task Create_PlanIsHigh_WithDomainAndNodeConfirmation()
    {
        var plan = await _create.PlanAsync(
            WebsiteTestData.PlanContext(
                [WebsiteTestData.Node()],
                CreateParameters(),
                "website.create"),
            CancellationToken.None);

        Assert.Equal("website.create", _create.Kind);
        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Equal("website", plan.TargetType);
        Assert.Contains("new.example.com", plan.Title, StringComparison.Ordinal);
        Assert.Equal("new.example.com @ node-a", plan.TypedConfirmationText);
        Assert.Contains(plan.Warnings, warning => warning.Contains("不会自动回滚", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_BatchNodes_IsRejected()
    {
        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _create.PlanAsync(
                WebsiteTestData.PlanContext(
                    [WebsiteTestData.Node("node-a"), WebsiteTestData.Node("node-b")],
                    CreateParameters(),
                    "website.create"),
                CancellationToken.None));

        Assert.Contains("单节点", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RuntimeWithoutRuntimeId_IsRejected()
    {
        var parameters = CreateParameters("runtime");
        parameters.Remove("runtimeId");

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _create.PlanAsync(
                WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters, "website.create"),
                CancellationToken.None));

        Assert.Contains("runtimeId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ExecutePostsWebsiteBody()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("api/v2/websites", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));

        var step = await _create.ExecuteAsync(
            WebsiteTestData.NodeContext(node, CreateParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("建站请求已提交", step.Message, StringComparison.Ordinal);
        var call = Assert.Single(factory.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("api/v2/websites", call.Path);
        Assert.NotNull(call.Body);
        Assert.Contains("\"type\":\"static\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"alias\":", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"webSiteGroupID\":2", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"appType\":\"installed\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"taskID\":", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"www.new.example.com\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DomainPrimary_PlanIsHigh_AndExecuteReadsCurrentDetail()
    {
        var parameters = WebsiteTestData.TargetParameters(
            extras: [("primaryDomain", "main.example.com")]);

        var plan = await _domainPrimary.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters, "domain.primary"),
            CancellationToken.None);

        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Contains("main.example.com", plan.Summary, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory()
            .Respond("websites/update", "{}")
            .Respond("websites/3", WebsiteTestJsonData.Detail);
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));

        var step = await _domainPrimary.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal("GET", factory.Calls[0].Method);
        Assert.Contains("websites/3", factory.Calls[0].Path, StringComparison.Ordinal);
        Assert.Equal("POST", factory.Calls[1].Method);
        var body = factory.Calls[1].Body;
        Assert.NotNull(body);
        Assert.Contains("\"primaryDomain\":\"main.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"id\":3", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslRequest_PlanIsHigh_AndExecuteCreatesThenObtains()
    {
        var parameters = new JsonObject
        {
            ["primaryDomain"] = "cert.example.com",
            ["otherDomains"] = new JsonArray("www.cert.example.com"),
            ["provider"] = "http",
            ["acmeAccountId"] = 5
        };

        var plan = await _sslRequest.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters, "ssl.request"),
            CancellationToken.None);

        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Equal("ssl", plan.TargetType);
        Assert.Equal("cert.example.com @ node-a", plan.TypedConfirmationText);

        var factory = new RecordingWebsiteClientFactory()
            .Respond("websites/ssl/obtain", "{}")
            .Respond("api/v2/websites/ssl", "{\"id\":7}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));

        var step = await _sslRequest.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal("api/v2/websites/ssl/obtain", factory.Calls[1].Path);
        Assert.Contains("\"ID\":7", factory.Calls[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslRequest_DnsManual_SkipsObtain()
    {
        var parameters = new JsonObject
        {
            ["primaryDomain"] = "cert.example.com",
            ["provider"] = "dnsManual",
            ["acmeAccountId"] = 5
        };

        var factory = new RecordingWebsiteClientFactory().Respond("api/v2/websites/ssl", "{\"id\":7}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));

        var step = await _sslRequest.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Single(factory.Calls);
        Assert.Contains("手动申请", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslRequest_DnsAccountWithoutAccountId_IsRejected()
    {
        var parameters = new JsonObject
        {
            ["primaryDomain"] = "cert.example.com",
            ["provider"] = "dnsAccount",
            ["acmeAccountId"] = 5
        };

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _sslRequest.PlanAsync(
                WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters, "ssl.request"),
                CancellationToken.None));

        Assert.Contains("dnsAccountId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslDisable_ExecuteReadsHttpsAndPostsDisabledConfig()
    {
        var parameters = WebsiteTestData.TargetParameters();
        var factory = new RecordingWebsiteClientFactory()
            .Respond("websites/3/https", WebsiteTestJsonData.Https);
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));

        var step = await _sslDisable.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("HTTPS 已关闭", step.Message, StringComparison.Ordinal);
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal("POST", factory.Calls[1].Method);
        Assert.Contains("\"enable\":false", factory.Calls[1].Body, StringComparison.Ordinal);
    }
}
