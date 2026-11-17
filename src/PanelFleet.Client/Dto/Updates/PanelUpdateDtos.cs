using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Updates;

// 1Panel 面板更新 DTO：全部宽松解析（字段缺失/类型变化不抛异常），只映射展示需要的字段。

/// <summary>
/// <c>POST /api/v2/core/settings/search/base</c> 响应：只取当前版本等基础设置字段。
/// </summary>
public sealed class PanelSettingBaseDto
{
    /// <summary>当前面板版本，如 <c>v2.3.2</c>。</summary>
    public string? SystemVersion { get; set; }

    /// <summary>版本分支，如 <c>community</c> / <c>international</c>。</summary>
    public string? Edition { get; set; }

    public string? PanelName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// <c>GET /api/v2/core/settings/upgrade</c> 响应：可用版本与更新说明。
/// <para><c>latestVersion</c> 为跨版本线的最新版本，<c>newVersion</c> 为当前版本线内的最新小版本；
/// 两者都为空表示当前已是最新（或面板离线/开发模式）。</para>
/// </summary>
public sealed class PanelUpgradeInfoDto
{
    /// <summary>开发模式/测试版可用版本（通常为空）。</summary>
    public string? TestVersion { get; set; }

    /// <summary>同版本线内可升级的小版本。</summary>
    public string? NewVersion { get; set; }

    /// <summary>跨版本线的最新版本。</summary>
    public string? LatestVersion { get; set; }

    /// <summary>目标版本的更新说明（Markdown 或纯文本）。</summary>
    public string? ReleaseNote { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>升级等命令类接口的空数据确认（1Panel 返回 <c>data:null</c>）。</summary>
public sealed class PanelUpdateAckDto
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
