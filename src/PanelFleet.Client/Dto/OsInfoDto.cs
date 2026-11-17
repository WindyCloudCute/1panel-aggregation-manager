using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto;

/// <summary>1Panel <c>/api/v2/dashboard/base/os</c> 响应（宽松解析）。</summary>
public sealed class OsInfoDto
{
    public string? Os { get; set; }

    public string? Platform { get; set; }

    public string? PlatformVersion { get; set; }

    public string? KernelVersion { get; set; }

    public string? KernelArch { get; set; }

    public string? Hostname { get; set; }

    public string? Version { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
