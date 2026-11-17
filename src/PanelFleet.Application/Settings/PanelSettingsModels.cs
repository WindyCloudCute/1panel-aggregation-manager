namespace PanelFleet.Application.Settings;

/// <summary>1Panel 面板设置页的一个只读字段。</summary>
public sealed record PanelSettingItem(
    string Key,
    string Label,
    string? Value,
    bool ReadOnly = false,
    bool Sensitive = false,
    string? Hint = null);

/// <summary>1Panel 面板设置页的一组字段。</summary>
public sealed record PanelSettingSection(
    string Key,
    string Title,
    string? Description,
    bool ReadOnly,
    IReadOnlyList<PanelSettingItem> Items);

/// <summary><c>GET /api/settings/panel/{nodeId}</c> 的响应主体（只读聚合视图）。</summary>
public sealed record PanelSettingsView(
    Guid NodeId,
    string NodeName,
    string BaseUrl,
    bool NodeEnabled,
    DateTimeOffset FetchedAt,
    long ElapsedMilliseconds,
    string? Error,
    IReadOnlyList<PanelSettingSection> Sections,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Unsupported);

/// <summary>设置读取结果：节点不存在时 <see cref="Found"/> 为 <c>false</c>。</summary>
public sealed record PanelSettingsResult(bool Found, PanelSettingsView? View);
