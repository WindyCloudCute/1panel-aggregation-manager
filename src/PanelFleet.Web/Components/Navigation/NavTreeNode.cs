namespace PanelFleet.Web.Components.Navigation;

/// <summary>导航树节点类型：分支（可展开）或叶子（可选中执行/查看）。</summary>
public enum NavTreeNodeKind
{
    Branch,
    Leaf
}

/// <summary>
/// 管理页左侧导航树的通用节点模型。
/// 页面负责把业务数据（节点/分组/资源）映射为树，NavTree 只负责渲染与交互。
/// </summary>
public sealed record NavTreeNode
{
    /// <summary>树内唯一 id（建议 "node:{guid}"、"resource:{nodeId}:{name}" 等；选中态以此为键）。</summary>
    public required string Id { get; init; }

    /// <summary>主标题。</summary>
    public required string Label { get; init; }

    /// <summary>次级说明（实例名、状态、地址等）。</summary>
    public string? Hint { get; init; }

    /// <summary>右侧徽标文字。</summary>
    public string? BadgeText { get; init; }

    /// <summary>徽标样式类（如 badge-ok / badge-warn / badge-error，复用全局样式）。</summary>
    public string? BadgeClass { get; init; }

    public NavTreeNodeKind Kind { get; init; } = NavTreeNodeKind.Branch;

    /// <summary>禁用节点：不可点击、置灰（例如已停用的服务器节点）。</summary>
    public bool IsDisabled { get; init; }

    /// <summary>正在加载子项（展开态转圈）。</summary>
    public bool IsLoading { get; init; }

    /// <summary>子项是否已加载；false 时首次展开会触发 OnToggle 让页面按需拉取。</summary>
    public bool ChildrenLoaded { get; init; } = true;

    /// <summary>节点级错误（例如子项加载失败），以红色小字展示。</summary>
    public string? Error { get; init; }

    /// <summary>该节点是否默认展开（详情页定位时可由页面控制）。</summary>
    public bool DefaultExpanded { get; init; }

    /// <summary>子节点。</summary>
    public IReadOnlyList<NavTreeNode> Children { get; init; } = [];

    /// <summary>叶子节点被点击时由页面处理；分支节点点击默认只展开/收起（除非页面显式处理）。</summary>
    public bool IsLeaf => Kind == NavTreeNodeKind.Leaf;

    /// <summary>可供全局搜索匹配的文本。</summary>
    public string SearchText =>
        string.Join(' ', new[] { Label, Hint, BadgeText }.Where(static part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>递归匹配过滤文本（自身或任一后代命中）。</summary>
    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Children.Any(child => child.Matches(filter));
    }
}
