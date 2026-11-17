using System.Net;
using System.Text.Json;

namespace PanelFleet.IntegrationTests;

/// <summary>P5/P6/P7/P8/P9/P10/P11/P12 管理页面与只读端点的接线冒烟测试。</summary>
public sealed class ManagementSmokeTests
{
    [Theory]
    [InlineData("/appstore", "应用商店")]
    [InlineData("/containers", "容器管理")]
    [InlineData("/panel-update", "面板更新")]
    [InlineData("/terminal", "终端")]
    [InlineData("/websites", "网站管理")]
    [InlineData("/databases", "数据库管理")]
    [InlineData("/cronjobs", "计划任务管理")]
    [InlineData("/logs", "系统日志")]
    public async Task ManagementPages_RenderForAdmin(string path, string expectedText)
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(expectedText, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/appstore/tags")]
    [InlineData("/api/containers/")]
    [InlineData("/api/panel-update/versions")]
    [InlineData("/api/websites")]
    [InlineData("/api/databases")]
    [InlineData("/api/cronjobs")]
    [InlineData("/api/logs")]
    public async Task ManagementReadEndpoints_WithoutAuthentication_Return401(string path)
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ContainerListEndpoint_AdminWithoutNodes_ReturnsEmptyList()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/api/containers/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("nodesTotal").GetInt32());
    }

    [Fact]
    public async Task PanelUpdateVersionsEndpoint_AdminWithoutNodes_ReturnsEmptyResult()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/api/panel-update/versions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
    }
}
