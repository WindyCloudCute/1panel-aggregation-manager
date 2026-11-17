using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto;

/// <summary>磁盘信息。</summary>
public sealed class DiskInfoDto
{
    public string? Device { get; set; }

    public string? Path { get; set; }

    public string? Type { get; set; }

    public long Total { get; set; }

    public long Used { get; set; }

    public long Free { get; set; }

    public double UsedPercent { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; set; }
}
