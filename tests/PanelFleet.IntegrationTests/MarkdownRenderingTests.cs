using PanelFleet.Web.Components.Shared;

namespace PanelFleet.IntegrationTests;

/// <summary>Markdown 渲染回归测试：更新说明必须渲染为 HTML，且不允许原始 HTML 注入。</summary>
public sealed class MarkdownRenderingTests
{
    [Fact]
    public void ReleaseNote_RendersMarkdownHeadingsAndLists()
    {
        const string markdown = """
            ## 更新时间

            * 优化大模型 GPU 调度
            * 修复节点恢复问题

            **重要**
            """;

        var html = MarkdownView.RenderHtml(markdown);

        Assert.Contains("<h2", html, StringComparison.Ordinal);
        Assert.Contains("<li>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("## 更新时间", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseNote_RawHtmlIsDisabled()
    {
        var html = MarkdownView.RenderHtml("<script>alert('x')</script>\n\n正常内容");

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("正常内容", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseNote_TooLong_IsTruncated()
    {
        var html = MarkdownView.RenderHtml(new string('x', 200), maxLength: 50);

        Assert.Contains("说明过长，已截断", html, StringComparison.Ordinal);
    }
}
