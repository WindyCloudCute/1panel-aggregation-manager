using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;
using PanelFleet.Application.Websites;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Websites;

/// <summary>网站管理写操作处理器单元测试：Plan（标题/摘要/风险/目标）与 Execute（请求体/结果）。</summary>
public sealed class WebsiteOperationHandlerTests
{
    private readonly WebsiteStartHandler _start = new();
    private readonly WebsiteStopHandler _stop = new();
    private readonly WebsiteUpdateHandler _update = new();
    private readonly WebsitePhpVersionHandler _php = new();
    private readonly DomainCreateHandler _domainCreate = new();
    private readonly DomainUpdateHandler _domainUpdate = new();
    private readonly DomainDeleteHandler _domainDelete = new();
    private readonly ProxyCreateHandler _proxyCreate = new();
    private readonly ProxyUpdateHandler _proxyUpdate = new();
    private readonly ProxyDeleteHandler _proxyDelete = new();
    private readonly ProxyStatusHandler _proxyStatus = new();
    private readonly SslApplyHandler _sslApply = new();
    private readonly SslDeployHandler _sslDeploy = new();

    [Fact]
    public async Task Start_PlanIsMedium_AndExecutePostsOperate()
    {
        var plan = await _start.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], WebsiteTestData.TargetParameters()),
            CancellationToken.None);

        Assert.Equal("website.start", _start.Kind);
        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Equal("website", plan.TargetType);
        Assert.Contains("启动网站 example.com", plan.Title, StringComparison.Ordinal);
        Assert.Single(plan.Targets);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/operate", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _start.ExecuteAsync(
            WebsiteTestData.NodeContext(node, WebsiteTestData.TargetParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Equal("3", step.TargetId);
        Assert.Contains("启动指令已提交", step.Message, StringComparison.Ordinal);
        var body = Assert.Single(factory.Calls).Body;
        Assert.NotNull(body);
        Assert.Contains("\"id\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"operate\":\"start\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ExecutePostsStop_AndBatchIsCritical()
    {
        var batchPlan = await _stop.PlanAsync(
            WebsiteTestData.PlanContext(
                [WebsiteTestData.Node("node-a"), WebsiteTestData.Node("node-b")],
                WebsiteTestData.TargetParameters()),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Critical, batchPlan.Risk);
        Assert.Equal(2, batchPlan.Targets.Count);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/operate", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _stop.ExecuteAsync(
            WebsiteTestData.NodeContext(node, WebsiteTestData.TargetParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("\"operate\":\"stop\"", Assert.Single(factory.Calls).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_Execute_PanelError_ReturnsFailedStep()
    {
        var factory = new RecordingWebsiteClientFactory().Fail("websites/operate", PanelErrorKind.PanelServerError);
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));

        var step = await _start.ExecuteAsync(
            WebsiteTestData.NodeContext(node, WebsiteTestData.TargetParameters(), client),
            CancellationToken.None);

        Assert.False(step.Success);
        Assert.Equal(PanelErrorKind.PanelServerError, step.Error?.Kind);
    }

    [Fact]
    public async Task Start_MissingWebsiteId_Throws()
    {
        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _start.PlanAsync(
                WebsiteTestData.PlanContext([WebsiteTestData.Node()], new JsonObject { ["name"] = "example.com" }),
                CancellationToken.None));
        Assert.Contains("websiteId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PlanAndExecute_BuildsUpdateBody()
    {
        var parameters = WebsiteTestData.TargetParameters(
            extras:
            [
                ("primaryDomain", "new.example.com"),
                ("remark", "主站"),
                ("webSiteGroupId", 2),
                ("expireDate", "2027-06-30"),
                ("favorite", true),
                ("IPV6", true)
            ]);

        var plan = await _update.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("修改网站 example.com", plan.Title, StringComparison.Ordinal);
        Assert.Contains("new.example.com", plan.Summary, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/update", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _update.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.NotNull(body);
        Assert.Contains("\"primaryDomain\":\"new.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"webSiteGroupId\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"expireDate\":\"2027-06-30\"", body, StringComparison.Ordinal);
        Assert.Contains("\"favorite\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"IPV6\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_InvalidDomain_Throws()
    {
        var parameters = WebsiteTestData.TargetParameters(extras: [("primaryDomain", "http://bad.example.com")]);
        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _update.PlanAsync(
                WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
                CancellationToken.None));
        Assert.Contains("域名格式不合法", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhpVersion_PlanAndExecute_BuildsBody()
    {
        var parameters = WebsiteTestData.TargetParameters(extras: [("runtimeId", 2)]);
        var plan = await _php.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("切换 PHP 版本", plan.Title, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/php/version", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _php.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.Contains("\"websiteID\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"runtimeID\":2", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DomainCreate_PlanMedium_AndExecuteBody()
    {
        var parameters = WebsiteTestData.TargetParameters(
            extras: [("domain", "new.example.com"), ("port", 80), ("ssl", true)]);

        var plan = await _domainCreate.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("new.example.com", plan.Summary, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/domains", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _domainCreate.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.Contains("\"websiteID\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"new.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"port\":80", body, StringComparison.Ordinal);
        Assert.Contains("\"ssl\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DomainUpdate_ExecuteBody()
    {
        var parameters = WebsiteTestData.TargetParameters(
            extras: [("domainId", 21), ("domain", "site-a.example.com"), ("ssl", false)]);

        var plan = await _domainUpdate.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Medium, plan.Risk);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/domains/update", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _domainUpdate.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.Contains("\"id\":21", body, StringComparison.Ordinal);
        Assert.Contains("\"ssl\":false", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DomainDelete_PlanIsHigh_AndExecuteBody()
    {
        var parameters = WebsiteTestData.TargetParameters(
            extras: [("domainId", 22), ("domain", "www.site-a.example.com")]);

        var plan = await _domainDelete.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
            CancellationToken.None);
        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Contains("删除网站域名", plan.Title, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/domains/del", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _domainDelete.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("\"id\":22", Assert.Single(factory.Calls).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyCreate_PlanMedium_AndExecuteOperateCreate()
    {
        var parameters = ProxyParameters();

        var plan = await _proxyCreate.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("新增反向代理", plan.Title, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/proxies/update", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _proxyCreate.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.NotNull(body);
        Assert.Contains("\"operate\":\"create\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"api\"", body, StringComparison.Ordinal);
        Assert.Contains("\"match\":\"/api\"", body, StringComparison.Ordinal);
        Assert.Contains("\"proxyPass\":\"http://127.0.0.1:8080\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyUpdate_ExecuteOperateEdit()
    {
        var factory = new RecordingWebsiteClientFactory().Respond("websites/proxies/update", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _proxyUpdate.ExecuteAsync(
            WebsiteTestData.NodeContext(node, ProxyParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("\"operate\":\"edit\"", Assert.Single(factory.Calls).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyDelete_PlanHigh_AndExecuteBody()
    {
        var plan = await _proxyDelete.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], ProxyParameters()),
            CancellationToken.None);
        Assert.Equal(OperationRisk.High, plan.Risk);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/proxies/delete", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _proxyDelete.ExecuteAsync(
            WebsiteTestData.NodeContext(node, ProxyParameters(), client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.Contains("\"id\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"api\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyStatus_ExecuteBody()
    {
        var parameters = ProxyParameters(("status", "disable"));
        var factory = new RecordingWebsiteClientFactory().Respond("websites/proxies/status", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _proxyStatus.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        Assert.Contains("\"status\":\"disable\"", Assert.Single(factory.Calls).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyCreate_InvalidProxyPass_Throws()
    {
        var parameters = ProxyParameters(("proxyPass", "ftp://bad"));
        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _proxyCreate.PlanAsync(
                WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters),
                CancellationToken.None));
        Assert.Contains("proxyPass", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslApply_PlanMedium_AndExecuteBody()
    {
        var parameters = new JsonObject
        {
            ["sslId"] = 2,
            ["primaryDomain"] = "*.example.com",
            ["skipDnsCheck"] = true
        };

        var plan = await _sslApply.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters, kind: "ssl.apply"),
            CancellationToken.None);
        Assert.Equal(OperationRisk.Medium, plan.Risk);
        Assert.Contains("申请 SSL 证书", plan.Title, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/ssl/obtain", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _sslApply.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.Contains("\"ID\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"skipDNSCheck\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslDeploy_PlanHigh_AndExecuteHttpsBody()
    {
        var parameters = new JsonObject
        {
            ["websiteId"] = 11,
            ["name"] = "site-a.example.com",
            ["sslId"] = 2,
            ["httpConfig"] = "HTTPToHTTPS",
            ["SSLProtocol"] = new JsonArray("TLSv1.2", "TLSv1.3")
        };

        var plan = await _sslDeploy.PlanAsync(
            WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters, kind: "ssl.deploy"),
            CancellationToken.None);
        Assert.Equal(OperationRisk.High, plan.Risk);
        Assert.Contains("部署 SSL 证书", plan.Title, StringComparison.Ordinal);

        var factory = new RecordingWebsiteClientFactory().Respond("websites/11/https", "{}");
        var node = WebsiteTestData.Node();
        var client = factory.Create(WebsiteTestData.Descriptor(node));
        var step = await _sslDeploy.ExecuteAsync(
            WebsiteTestData.NodeContext(node, parameters, client),
            CancellationToken.None);

        Assert.True(step.Success);
        var body = Assert.Single(factory.Calls).Body;
        Assert.NotNull(body);
        Assert.Contains("\"websiteId\":11", body, StringComparison.Ordinal);
        Assert.Contains("\"websiteSSLId\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"existed\"", body, StringComparison.Ordinal);
        Assert.Contains("\"httpConfig\":\"HTTPToHTTPS\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SslDeploy_InvalidHttpConfig_Throws()
    {
        var parameters = new JsonObject
        {
            ["websiteId"] = 11,
            ["sslId"] = 2,
            ["httpConfig"] = "Bad"
        };

        var exception = await Assert.ThrowsAsync<OperationValidationException>(
            () => _sslDeploy.PlanAsync(
                WebsiteTestData.PlanContext([WebsiteTestData.Node()], parameters, kind: "ssl.deploy"),
                CancellationToken.None));
        Assert.Contains("HTTPS 访问方式", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DomainDelete_Batch_IsCritical()
    {
        var parameters = WebsiteTestData.TargetParameters(extras: [("domainId", 22)]);
        var plan = await _domainDelete.PlanAsync(
            WebsiteTestData.PlanContext(
                [WebsiteTestData.Node("node-a"), WebsiteTestData.Node("node-b")],
                parameters),
            CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, plan.Risk);
    }

    private static JsonObject ProxyParameters(params (string Key, object? Value)[] overrides)
    {
        var parameters = WebsiteTestData.TargetParameters(
            extras:
            [
                ("proxyName", "api"),
                ("match", "/api"),
                ("proxyPass", "http://127.0.0.1:8080"),
                ("proxyHost", "$host"),
                ("enable", true)
            ]);

        foreach (var (key, value) in overrides)
        {
            parameters[key] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value));
        }

        return parameters;
    }
}
