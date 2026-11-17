using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto;

/// <summary>1Panel <c>/api/v2/settings/search</c> 响应（只取版本字段）。</summary>
public sealed class SettingsSearchDto
{
    public string? SystemVersion { get; set; }

    /// <summary>默认监控 IO/网络选项，P3 总览可直接复用。</summary>
    public string? DefaultIO { get; set; }

    public string? DefaultNetwork { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
