using System.Text;
using System.Text.RegularExpressions;

namespace PanelFleet.Client.UnitTests.Ui;

/// <summary>
/// P20 静态卫生门禁：UI 源码不得出现硬编码颜色、emoji 图标与调试日志。
/// 设计令牌集中在 <c>wwwroot/app.css</c> 的 <c>:root</c>（该文件是令牌定义处，跳过）；
/// 组件样式允许 <c>var(--token, #hex)</c> 回退写法，其余裸色值一律视为回归。
/// </summary>
public sealed class SourceHygieneTests
{
    private static readonly string WebRoot = Path.Combine(TestRepository.Root, "src", "PanelFleet.Web");

    private static readonly Regex VarFallback = new(
        @"var\(--[A-Za-z0-9-]+,\s*#[0-9A-Fa-f]{3,8}\)",
        RegexOptions.Compiled);

    private static readonly Regex HexColor = new(
        @"(?<!&)#[0-9A-Fa-f]{3,8}\b",
        RegexOptions.Compiled);

    private static readonly Regex ConsoleLog = new(
        @"\bconsole\.(log|debug|trace|info)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// P20 之前（HEAD a3c2fb7）已存在的历史色值，属既有技术债，清理不在本次质量门禁范围内。
    /// 该清单只允许缩短：任何新文件/新行出现裸色值都会被本测试拦下（WebsiteCreateWizard 的 #fff 即由此发现并已修）。
    /// </summary>
    private static readonly HashSet<string> LegacyHardcodedColorFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "src/PanelFleet.Web/Components/AppStore/AppTaskProgress.razor.css",
        "src/PanelFleet.Web/Components/AppStore/InstallAppForm.razor.css",
        "src/PanelFleet.Web/Components/AppStore/InstalledParamsEditor.razor.css",
        "src/PanelFleet.Web/Components/Cronjobs/CronjobForm.razor.css",
        "src/PanelFleet.Web/Components/Cronjobs/CronjobRecordsPanel.razor.css",
        "src/PanelFleet.Web/Components/Cronjobs/CronjobStatusBadge.razor.css",
        "src/PanelFleet.Web/Components/Cronjobs/CronjobTable.razor.css",
        "src/PanelFleet.Web/Components/Cronjobs/CronjobTypeBadge.razor.css",
        "src/PanelFleet.Web/Components/Logs/LogResultView.razor.css",
        "src/PanelFleet.Web/Components/Pages/AppStore.razor.css",
        "src/PanelFleet.Web/Components/Pages/Websites.razor.css",
        "src/PanelFleet.Web/Components/Websites/DomainFormDialog.razor.css",
        "src/PanelFleet.Web/Components/Websites/ProxyFormDialog.razor.css",
        "src/PanelFleet.Web/Components/Websites/SslDeployDialog.razor.css",
        "src/PanelFleet.Web/Components/Websites/WebsiteEditDialog.razor.css",
        "src/PanelFleet.Web/Components/Websites/WebsiteTable.razor.css"
    };

    [Fact]
    public void SourceFiles_AreDiscovered()
    {
        Assert.True(Directory.Exists(WebRoot), $"未找到 Web 项目目录：{WebRoot}");
        var files = EnumerateSourceFiles();
        Assert.NotEmpty(files);
        Assert.Contains(files, static file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, static file => file.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoHardcodedHexColors_OutsideTokenFallbacks()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles().Where(static file => IsStyleOrMarkup(file)))
        {
            var relative = Relative(file);
            if (LegacyHardcodedColorFiles.Contains(relative))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);

            for (var index = 0; index < lines.Length; index++)
            {
                var stripped = VarFallback.Replace(lines[index], string.Empty);
                var match = HexColor.Match(stripped);
                if (match.Success)
                {
                    violations.Add($"{relative}:{index + 1} 硬编码颜色 {match.Value}（应使用 var(--token) 或 var(--token, #hex) 回退）");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现硬编码颜色（除 app.css 令牌定义与 var() 回退外不允许）：" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void NoEmojiIcons_InUiSource()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            var text = File.ReadAllText(file);
            var line = 1;
            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                if (current == '\n')
                {
                    line++;
                }

                if (!IsEmoji(text, index))
                {
                    continue;
                }

                var codePoint = char.ConvertToUtf32(text, index);
                violations.Add($"{Relative(file)}:{line} emoji U+{codePoint:X4}（图标请用内联 SVG）");
                index += char.IsSurrogatePair(text, index) ? 1 : 0;
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现 emoji 图标（UI 源码不允许）：" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void NoConsoleLogging_InUiSource()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (ConsoleLog.IsMatch(lines[index]))
                {
                    violations.Add($"{Relative(file)}:{index + 1} 调试日志：{lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现 console 调试日志：" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static bool IsEmoji(string text, int index)
    {
        var current = text[index];
        int codePoint;
        if (char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            codePoint = char.ConvertToUtf32(current, text[index + 1]);
        }
        else if (char.IsSurrogate(current))
        {
            return false;
        }
        else
        {
            codePoint = current;
        }

        // 1F000–1FAFF：emoji 主区（含 🚀 📦 ⚠ 等）；FE0F：emoji 变体选择符；2B50/2705/274C 等高频状态符号。
        return codePoint is >= 0x1F000 and <= 0x1FAFF
               or 0xFE0F
               or 0x2B50
               or 0x2705
               or 0x274C
               or 0x2757;
    }

    private static bool IsStyleOrMarkup(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".razor", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".js", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateSourceFiles()
    {
        var results = new List<string>();
        Collect(WebRoot, results);
        return results;
    }

    private static void Collect(string directory, List<string> results)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("app.css", StringComparison.OrdinalIgnoreCase))
            {
                // 设计令牌定义处（:root 变量），色值本就集中在这里。
                continue;
            }

            var extension = Path.GetExtension(name);
            if (extension is ".razor" or ".css" or ".js" or ".cs" or ".html")
            {
                results.Add(file);
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or "vendor" or "node_modules" or "lib")
            {
                continue;
            }

            Collect(sub, results);
        }
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(TestRepository.Root, file).Replace('\\', '/');
}
