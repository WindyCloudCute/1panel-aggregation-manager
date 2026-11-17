using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto;

/// <summary>1Panel 当前资源指标（宽松解析）。</summary>
public sealed class DashboardCurrentDto
{
    public double CpuUsedPercent { get; set; }

    public double MemoryUsedPercent { get; set; }

    public long MemoryTotal { get; set; }

    public long MemoryUsed { get; set; }

    public long MemoryAvailable { get; set; }

    public double Load1 { get; set; }

    public double Load5 { get; set; }

    public double Load15 { get; set; }

    public double NetBytesRecv { get; set; }

    public double NetBytesSent { get; set; }

    public long Uptime { get; set; }

    public DateTimeOffset? ShotTime { get; set; }

    public List<DiskInfoDto>? DiskData { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
