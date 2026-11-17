using System.Text.RegularExpressions;

namespace PanelFleet.Client.UnitTests.Ui;

/// <summary>
/// 历史 bug 类回归（commit 1cc67d0）：Razor 组件的 <c>string</c> 类型 <c>[Parameter]</c> 被以
/// 「标识符字面量」赋值，例如 <c>&lt;CronjobStatusBadge Status="row.Status" /&gt;</c>。
/// Blazor 对 string 参数会把普通文本当字面量，页面就会直接显示 <c>row.Status</c>；
/// 必须写成 <c>Status="@row.Status"</c>。本测试静态扫描 <c>src/PanelFleet.Web/Components/**/*.razor</c>，
/// 允许真正的字面量常量（如 <c>MetricBar Label="CPU"</c>、<c>Text="已停止"</c>）。
/// </summary>
public sealed class RazorStringParameterBindingTests
{
    [Fact]
    public void Components_DoNotAssignStringParametersToBareExpressions()
    {
        var componentsDir = Path.Combine(TestRepository.Root, "src", "PanelFleet.Web", "Components");
        Assert.True(Directory.Exists(componentsDir), $"未找到 Web 组件目录：{componentsDir}");

        var files = Directory.GetFiles(componentsDir, "*.razor", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var sources = files.ToDictionary(
            static file => file,
            static file => File.ReadAllText(file),
            StringComparer.OrdinalIgnoreCase);

        var declarations = RazorBindingScanner.ReadStringParameters(sources);

        // 防止规则空转：仓库中确实存在带 string 参数的组件（NavTree、MetricBar 等）。
        Assert.NotEmpty(declarations);

        var violations = RazorBindingScanner.Scan(sources, declarations);
        Assert.True(
            violations.Count == 0,
            "检测到 Razor string 参数被当作字面量传入（缺少 @），共 " + violations.Count + " 处：\n"
                + string.Join("\n", violations.Select(static v => "  " + v))
                + "\n修复方式：把 Attr=\"row.Status\" 写成 Attr=\"@row.Status\"；真正的字面量常量无需修改。");
    }

    [Fact]
    public void Scanner_DetectsMissingAtAndAllowsLiteralConstants()
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MetricBar.razor"] = """
                @code {
                    [Parameter]
                    public string Label { get; set; } = string.Empty;

                    [Parameter]
                    public string Text { get; set; } = string.Empty;
                }
                """,
            ["StatusBadge.razor"] = """
                @code {
                    [Parameter, EditorRequired]
                    public string? Status { get; set; }
                }
                """,
            ["Page.razor"] = """
                <StatusBadge Status="row.Status" />
                <StatusBadge Status="@row.Status" />
                <StatusBadge Status="_status" />
                <MetricBar Label="CPU" Text="已停止" />
                <MetricBar Text="string.Empty" />
                """
        };

        var declarations = RazorBindingScanner.ReadStringParameters(sources);
        var violations = RazorBindingScanner.Scan(sources, declarations);

        Assert.Equal(3, violations.Count);
        Assert.Contains(violations, static v => v.Contains("Status=\"row.Status\"", StringComparison.Ordinal));
        Assert.Contains(violations, static v => v.Contains("Status=\"_status\"", StringComparison.Ordinal));
        Assert.Contains(violations, static v => v.Contains("Text=\"string.Empty\"", StringComparison.Ordinal));
        // 正确绑定与合法字面量常量不得误报。
        Assert.DoesNotContain(violations, static v => v.Contains("@row.Status", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, static v => v.Contains("Label=\"CPU\"", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, static v => v.Contains("Text=\"已停止\"", StringComparison.Ordinal));
    }
}

/// <summary>从测试输出目录向上定位仓库根（存在 PanelFleet.slnx 的目录）。</summary>
internal static class TestRepository
{
    public static string Root { get; } = Find();

    private static string Find()
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

        throw new InvalidOperationException(
            "无法从测试输出目录向上定位 PanelFleet.slnx，无法执行 Razor 绑定扫描：" + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Razor 字符串参数缺 @ 扫描器。解析 .razor 中的组件字符串参数声明，再扫描组件标签的
/// <c>Attr="..."</c> 属性值，命中「像 C# 表达式但没写 @」的字面量即判为违规。
/// </summary>
internal static class RazorBindingScanner
{
    // [Parameter]（可带其它特性）public string / string? Prop { get; ... }
    private static readonly Regex StringParameterDeclaration = new(
        @"\[Parameter[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+string\??\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{",
        RegexOptions.Compiled);

    // 组件标签：首字母大写的标签名后必须跟空白、/ 或 >（排除泛型 <T> 与全限定名）。
    private static readonly Regex ComponentTagStart = new(
        @"<(?<name>[A-Z][A-Za-z0-9_]*)\b(?=[\s/>])",
        RegexOptions.Compiled);

    private static readonly Regex AttributePattern = new(
        @"(?<name>[A-Za-z_][A-Za-z0-9_:]*)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s=/>]+))",
        RegexOptions.Compiled);

    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    // 允许的字面量常量白名单（组件名 + 参数名），默认空：常规字面量（CPU / 已停止 / v2.3.2）不会被判定为代码。
    private static readonly HashSet<(string Component, string Parameter)> Exemptions = [];

    /// <summary>返回 组件名 → 该组件声明的 string 参数集合。</summary>
    public static Dictionary<string, HashSet<string>> ReadStringParameters(IReadOnlyDictionary<string, string> sources)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, text) in sources)
        {
            var componentName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(componentName))
            {
                continue;
            }

            var withoutComments = StripComments(text);
            foreach (Match match in StringParameterDeclaration.Matches(withoutComments))
            {
                if (!result.TryGetValue(componentName, out var parameters))
                {
                    parameters = new HashSet<string>(StringComparer.Ordinal);
                    result[componentName] = parameters;
                }

                parameters.Add(match.Groups["name"].Value);
            }
        }

        return result;
    }

    /// <summary>返回人类可读的违规列表（文件: 标签属性片段）。</summary>
    public static List<string> Scan(
        IReadOnlyDictionary<string, string> sources,
        IReadOnlyDictionary<string, HashSet<string>> stringParameters)
    {
        if (stringParameters.Count == 0)
        {
            return [];
        }

        var violations = new List<string>();
        foreach (var (path, text) in sources.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            var stripped = StripComments(text);
            foreach (Match tagStart in ComponentTagStart.Matches(stripped))
            {
                var componentName = tagStart.Groups["name"].Value;
                if (!stringParameters.TryGetValue(componentName, out var parameters) || parameters.Count == 0)
                {
                    continue;
                }

                var tag = ReadTag(stripped, tagStart.Index + tagStart.Length);
                if (tag is null)
                {
                    continue;
                }

                foreach (Match attribute in AttributePattern.Matches(tag))
                {
                    if (!parameters.Contains(attribute.Groups["name"].Value))
                    {
                        continue;
                    }

                    var value = attribute.Groups["dq"].Success
                        ? attribute.Groups["dq"].Value
                        : attribute.Groups["sq"].Success
                            ? attribute.Groups["sq"].Value
                            : attribute.Groups["bare"].Value;

                    if (IsMissingAt(value)
                        && !Exemptions.Contains((componentName, attribute.Groups["name"].Value)))
                    {
                        var line = CountLine(text, tagStart.Index);
                        violations.Add(
                            $"{fileName}:{line} <{componentName} {attribute.Groups["name"].Value}=\"{value}\" />");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// 值是否「像 C# 表达式却没写 @」：成员访问（row.Status / _x.Engine / list?.Rows）、
    /// 私有字段（_drawerError）、this.X、方法调用（Format(x)）。
    /// 允许纯字面量：CPU、已停止、example.com（尾段小写）、v2.3.2（含数字段）等。
    /// </summary>
    internal static bool IsMissingAt(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length == 0 || value[0] == '@')
        {
            return false;
        }

        if (value.StartsWith("value=\"@", StringComparison.Ordinal))
        {
            return false;
        }

        return Regex.IsMatch(value, @"^_[A-Za-z0-9_]+$")
            || Regex.IsMatch(value, @"^this\.[A-Za-z_][A-Za-z0-9_]*$")
            || Regex.IsMatch(value, @"^[a-z_][A-Za-z0-9_]*\??\.[A-Z][A-Za-z0-9_]*(\.[A-Z][A-Za-z0-9_]*)*$")
            || Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_.]*\([^)]*\)$");
    }

    // 注释替换为等长空白（保留换行）以便行号与原文一致。
    private static string StripComments(string text)
    {
        static string Blank(Match match)
        {
            var chars = match.Value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '\n')
                {
                    chars[i] = ' ';
                }
            }

            return new string(chars);
        }

        var withoutRazor = RazorComment.Replace(text, Blank);
        return HtmlComment.Replace(withoutRazor, Blank);
    }

    /// <summary>从标签名后的位置读取到未加引号的 '>'，返回标签体（不含首尾尖括号）。</summary>
    private static string? ReadTag(string stripped, int start)
    {
        var quote = '\0';
        for (var i = start; i < stripped.Length; i++)
        {
            var c = stripped[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
            {
                return stripped[start..i];
            }
        }

        return null;
    }

    private static int CountLine(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
