using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P20 波次（用户 10 项反馈修复）的页面级质量门禁。
/// 覆盖 NavigationTreeUiTests 之外的新增交互面：
/// 应用商店三个 tab、设置工作台、容器工作台（Compose tab）、抽屉/日志的 scoped CSS 是否被打包。
/// 断言页面服务端预渲染（SSR）成功且不含 Blazor 渲染错误，不依赖客户端 JS。
/// </summary>
public sealed class P20PagesSsrGateTests
{
    /// <summary>路径、页面标题、证明页面专属结构已渲染的标记。</summary>
    public static TheoryData<string, string, string> SsrPages => new()
    {
        { "/containers", "容器管理", "container-workbench" },
        { "/websites", "网站管理", "workbench-tree" },
        { "/cronjobs", "计划任务", "workbench-tree" },
        { "/databases", "数据库管理", "db-layout" },
        { "/appstore", "应用商店", "appstore-tabs" },
        { "/appstore?tab=installed", "应用商店", "已安装应用" },
        { "/appstore?tab=upgrade", "应用商店", "可升级" },
        { "/panel-update", "面板更新", "检查更新" },
        { "/terminal", "终端", "terminal-" },
        { "/logs", "系统日志", "logs-layout" },
        { "/settings", "设置", "settings-panel" },
        { "/nodes", "节点", "page-shell" },
        { "/", "总览", "overview-grid" }
    };

    [Theory]
    [MemberData(nameof(SsrPages))]
    public async Task P20Page_SsrRendersTitleSkeletonAndNoBlazorError(string path, string expectedTitle, string expectedMarker)
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        await CreateNodeAsync(client, "p20-ssr-node");

        var html = await GetHtmlAsync(client, path);

        Assert.Contains("<title>", html, StringComparison.Ordinal);
        Assert.Contains(expectedTitle, html, StringComparison.Ordinal);
        Assert.Contains(expectedMarker, html, StringComparison.Ordinal);
        AssertNoRenderError(html);
    }

    /// <summary>
    /// 问题 7：`?tab=upgrade` 深链必须直接落在「可升级」tab（独立升级页 + 批量升级入口），
    /// 而不是回落到应用商店 tab。断言升级面板独有的工具条文案。
    /// </summary>
    [Fact]
    public async Task AppStore_UpgradeDeepLink_ActivatesUpgradeTab()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        await CreateNodeAsync(client, "p20-upgrade-node");

        var html = await GetHtmlAsync(client, "/appstore?tab=upgrade");

        Assert.Contains("可升级", html, StringComparison.Ordinal);
        Assert.Contains("刷新可升级", html, StringComparison.Ordinal);
        Assert.Contains("升级前备份", html, StringComparison.Ordinal);
        Assert.Contains("批量升级", html, StringComparison.Ordinal);
        // 升级面板与「商店」面板互斥渲染：商店搜索框不应出现，证明 tab 已被 query 切换到 upgrade。
        Assert.DoesNotContain("应用名称…", html, StringComparison.Ordinal);
        AssertNoRenderError(html);
    }

    /// <summary>
    /// 容器工作台必须同时渲染「容器 / Compose 项目」两个 tab（问题 5 的入口），
    /// 且批量操作栏存在（问题 1 的触发点），否则抽屉/浮层修复无从触发。
    /// </summary>
    [Fact]
    public async Task ContainersPage_RendersWorkbenchTabsAndBatchBar()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        await CreateNodeAsync(client, "p20-container-node");

        var html = await GetHtmlAsync(client, "/containers");

        Assert.Contains("container-workbench", html, StringComparison.Ordinal);
        Assert.Contains("container-tabs", html, StringComparison.Ordinal);
        Assert.Contains("Compose 项目", html, StringComparison.Ordinal);
        Assert.Contains("container-main", html, StringComparison.Ordinal);
        AssertNoRenderError(html);
    }

    /// <summary>设置页两区（PanelFleet 设置 / 1Panel 面板设置）都必须在 SSR 输出中可见（问题 10）。</summary>
    [Fact]
    public async Task SettingsPage_RendersFleetAndPanelSections()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateAdminClient();
        await CreateNodeAsync(client, "p20-settings-node");

        var html = await GetHtmlAsync(client, "/settings");

        Assert.Contains("PanelFleet 设置", html, StringComparison.Ordinal);
        Assert.Contains("1Panel 面板设置", html, StringComparison.Ordinal);
        Assert.Contains("settings-workspace", html, StringComparison.Ordinal);
        Assert.Contains("settings-card-grid", html, StringComparison.Ordinal);
        AssertNoRenderError(html);
    }

    /// <summary>
    /// 抽屉/日志/终端/Compose 编辑器的 scoped CSS 必须进入 <c>PanelFleet.Web.styles.css</c> 打包产物，
    /// 否则 `position: fixed` 浮层在发布包里会退化为普通文档流（问题 1/2/4 的回归点）。
    /// 静态资源在测试宿主里由 MapStaticAssets 提供（带指纹），这里直接断言构建产物更稳定。
    /// </summary>
    [Fact]
    public void ScopedStyleBundle_ContainsDrawerLogAndComposeStyles()
    {
        var bundle = FindBuildArtifact("PanelFleet.Web.styles.css");
        var css = File.ReadAllText(bundle);

        foreach (var marker in new[]
                 {
                     ".log-drawer", ".log-lines", ".is-follow", ".is-fullscreen",
                     ".terminal-drawer", ".container-workbench", ".compose-editor-modal",
                     ".settings-panel"
                 })
        {
            Assert.Contains(marker, css, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 问题 1/2 的根因是祖先容器上的 transform 动画会让 position: fixed 失效；
    /// 门禁要求页面进出场只用 opacity 动画（page-fade），旧的 translateY 关键帧（page-in）不得复活。
    /// </summary>
    [Fact]
    public void PageEnterAnimations_DoNotUseTransform()
    {
        var appCss = Path.Combine(FindRepositoryRoot(), "src", "PanelFleet.Web", "wwwroot", "app.css");
        Assert.True(File.Exists(appCss), $"未找到 app.css：{appCss}");
        var css = StripCssComments(File.ReadAllText(appCss));

        // 旧实现（会形成 containing block 的 transform 关键帧）不得回归。
        Assert.DoesNotContain("@keyframes page-in", css, StringComparison.Ordinal);
        Assert.DoesNotContain("animation: page-in", css, StringComparison.Ordinal);

        // .page-enter 必须使用纯透明度动画。
        var pageEnter = ExtractRule(css, ".page-enter");
        Assert.NotNull(pageEnter);
        Assert.Contains("page-fade", pageEnter, StringComparison.Ordinal);

        var pageKeyframes = ExtractKeyframes(css, "page-fade").ToList();
        Assert.NotEmpty(pageKeyframes);
        foreach (var keyframe in pageKeyframes)
        {
            Assert.DoesNotContain("transform", keyframe, StringComparison.OrdinalIgnoreCase);
        }

        // 同族残留（docs/19 §8）：PageShell 包裹 Nodes/Home/Audit/NodeForm/Settings 页面，
        // 其 .card/.form-card 入场动画若含 transform，会让内部 position:fixed 浮层（如 /nodes 删除确认框）失效。
        var pageShellCss = Path.Combine(FindRepositoryRoot(), "src", "PanelFleet.Web", "Components", "Nodes", "PageShell.razor.css");
        Assert.True(File.Exists(pageShellCss), $"未找到 PageShell.razor.css：{pageShellCss}");
        var pageShell = StripCssComments(File.ReadAllText(pageShellCss));

        // 入场动画本身不得用 transform；按钮/卡片的 hover 过渡 transform 不影响浮层定位，允许保留。
        foreach (var rule in ExtractRulesContaining(pageShell, "animation: page-rise"))
        {
            Assert.DoesNotContain("transform", rule, StringComparison.OrdinalIgnoreCase);
        }

        var pageRise = ExtractKeyframes(pageShell, "page-rise").ToList();
        Assert.NotEmpty(pageRise);
        foreach (var keyframe in pageRise)
        {
            Assert.DoesNotContain("transform", keyframe, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string StripCssComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>
    /// 返回所有主体包含 <paramref name="needle"/> 的 CSS 规则块（选择器 + 声明体），
    /// 用于「某动画的声明处不得带 transform」这类断言。
    /// </summary>
    private static IEnumerable<string> ExtractRulesContaining(string css, string needle)
    {
        const string pattern = @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}";
        foreach (Match match in Regex.Matches(css, pattern, RegexOptions.Singleline))
        {
            var body = match.Groups["body"].Value;
            if (body.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                yield return match.Groups["selector"].Value + "{" + body + "}";
            }
        }
    }

    private static string? ExtractRule(string css, string selector)
    {
        var match = Regex.Match(css, $"{Regex.Escape(selector)}\\s*\\{{(?<body>[^}}]*)\\}}", RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value : null;
    }

    private static string FindBuildArtifact(string fileName)
    {
        var results = Directory.GetFiles(
            Path.Combine(FindRepositoryRoot(), "src", "PanelFleet.Web", "obj"),
            fileName,
            SearchOption.AllDirectories);
        return results.FirstOrDefault()
            ?? throw new InvalidOperationException($"未找到构建产物 {fileName}，请先执行 build。");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PanelFleet.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（PanelFleet.slnx）。");
    }

    private static IEnumerable<string> ExtractKeyframes(string css, params string[] names)
    {
        foreach (var name in names)
        {
            var match = Regex.Match(css, $"@keyframes\\s+{Regex.Escape(name)}\\s*\\{{(?<body>[^@]*?)\\}}\\s*\\}}", RegexOptions.Singleline);
            if (match.Success)
            {
                yield return match.Groups["body"].Value;
            }
        }
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static void AssertNoRenderError(string html)
    {
        foreach (var marker in new[]
                 {
                     "An unhandled error has occurred",
                     "An error has occurred while processing your request",
                     "Unhandled exception",
                     "System.InvalidOperationException"
                 })
        {
            Assert.DoesNotContain(marker, html, StringComparison.OrdinalIgnoreCase);
        }
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
