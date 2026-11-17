using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// UI 重构（docs/18）回归门禁：管理页导航壳、页面标题与「左树 + 工作区」结构。
/// 这些断言在页面由旧布局（顶部节点下拉）重构为 NavTree 左树后仍然成立，防止树组件渲染异常导致整页白屏。
/// </summary>
public sealed class NavigationTreeUiTests
{
    /// <summary>路径、页面标题、骨架标记（左树/工作区的 CSS 类前缀，证明结构已渲染）。</summary>
    public static TheoryData<string, string, string> ManagementPages => new()
    {
        { "/containers", "容器管理", "workbench" },
        { "/websites", "网站管理", "workbench" },
        { "/databases", "数据库管理", "db-" },
        { "/cronjobs", "计划任务", "workbench" },
        { "/appstore", "应用商店", "nav-tree" },
        { "/logs", "系统日志", "nav-tree" },
        { "/panel-update", "面板更新", "update-" },
        { "/terminal", "终端", "terminal-" }
    };

    private static readonly string[] NavHrefs =
    [
        "/appstore",
        "/containers",
        "/panel-update",
        "/terminal",
        "/websites",
        "/databases",
        "/cronjobs",
        "/logs"
    ];

    [Theory]
    [MemberData(nameof(ManagementPages))]
    public async Task ManagementPage_RendersTitleNavShellAndNoBlazorError(string path, string expectedTitle, string expectedSkeleton)
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        // 有节点时管理页才渲染「左树 + 工作区」；无节点时是引导性空态卡片。
        await CreateNodeAsync(client, "ui-tree-node");

        var html = await GetHtmlAsync(client, path);

        // 页面标题（<PageTitle> 预渲染为 <title>）证明路由与页面组件正常执行到渲染阶段。
        Assert.Contains("<title>", html, StringComparison.Ordinal);
        Assert.Contains(expectedTitle, html, StringComparison.Ordinal);

        // 布局壳（NavMenu 在 MainLayout 中）必须随任意页面一起渲染。
        Assert.Contains("href=\"/containers\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs\"", html, StringComparison.Ordinal);

        // 页面骨架：UI 重构（docs/18）后是「左树 + 右工作区」，各页骨架类名不同，
        // 断言其专属标记出现即证明树/工作区已渲染（不再接受旧布局的 .page-header 兜底）。
        Assert.Contains(expectedSkeleton, html, StringComparison.Ordinal);

        AssertNoRenderError(html);
    }

    [Fact]
    public async Task NavMenu_ContainsAllManagementEntries()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var html = await GetHtmlAsync(client, "/logs");

        foreach (var href in NavHrefs)
        {
            Assert.Contains($"href=\"{href}\"", html, StringComparison.Ordinal);
        }

        foreach (var text in new[] { "应用商店", "容器管理", "面板更新", "终端", "网站管理", "数据库管理", "系统日志" })
        {
            Assert.Contains(text, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NavMenu_HasNoLegacyResourceEntries()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        var html = await GetHtmlAsync(client, "/logs");

        // 资源页已与管理页 1:1 合并（docs/19 问题 9），侧栏不得再有 /resources/* 重复入口。
        Assert.DoesNotContain("href=\"/resources/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"nav-group-title\">资源</div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_DoNotLeakBlazorRenderErrors()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();

        // 覆盖所有管理页 + 首页/节点/审计/设置，任何一页 SSR 抛异常都会落到 blazor-error-ui。
        foreach (var path in new[]
                 {
                     "/", "/nodes", "/audit", "/settings", "/containers", "/websites", "/databases",
                     "/cronjobs", "/appstore", "/logs", "/panel-update", "/terminal"
                 })
        {
            var html = await GetHtmlAsync(client, path);
            AssertNoRenderError(html);
        }
    }

    [Fact]
    public async Task Anonymous_HealthzAndLogin_ReturnOk()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var healthz = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthz.StatusCode);
        Assert.Equal("ok", await healthz.Content.ReadAsStringAsync());

        var login = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginHtml = WebUtility.HtmlDecode(await login.Content.ReadAsStringAsync());
        Assert.Contains("登录", loginHtml, StringComparison.Ordinal);
    }

    private static void AssertNoRenderError(string html)
    {
        Assert.DoesNotContain("An unhandled error has occurred", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("An error has occurred while processing your request", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.InvalidOperationException", html, StringComparison.Ordinal);
    }

    private static string Snippet(string html)
    {
        var flat = Regex.Replace(html, @"\s+", " ");
        return flat.Length <= 600 ? flat : flat[..600];
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Blazor 预渲染把动态文本中的非 ASCII 编码为 HTML 实体（如 &#x5BB9;），断言前先解码。
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task CreateNodeAsync(HttpClient client, string name)
    {
        var csrf = await client.GetAsync("/api/antiforgery/token");
        csrf.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString()!;
        var cookie = csrf.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First(value => value.StartsWith("PanelFleet.Antiforgery=", StringComparison.Ordinal)).Split(';')[0]
            : throw new InvalidOperationException("缺少防伪 Cookie。");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
        {
            Content = JsonContent.Create(new
            {
                name,
                baseUrl = $"https://{name}.example.test",
                apiKey = "test-api-key-1234",
                authMode = "Auto",
                tlsPolicy = "Strict",
                isEnabled = true,
                tags = Array.Empty<string>()
            })
        };
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
