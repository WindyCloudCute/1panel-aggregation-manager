using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto;

/// <summary>1Panel 面板基础信息与资源计数（宽松解析）。</summary>
public sealed class DashboardBaseDto
{
    public string? Hostname { get; set; }

    public string? Os { get; set; }

    public string? Platform { get; set; }

    public string? PlatformVersion { get; set; }

    /// <summary>发行版展示名（如 Debian GNU/Linux 13）；真实 v2.3.2 报文包含。</summary>
    public string? PrettyDistro { get; set; }

    public string? KernelVersion { get; set; }

    public string? KernelArch { get; set; }

    /// <summary>节点 IP（真实 v2.3.2 报文包含）。</summary>
    public string? IpV4Addr { get; set; }

    public string? CpuModelName { get; set; }

    public int CpuCores { get; set; }

    public int CpuLogicalCores { get; set; }

    public int WebsiteNumber { get; set; }

    public int AppInstalledNumber { get; set; }

    public int DatabaseNumber { get; set; }

    public int CronjobNumber { get; set; }

    public string? Version { get; set; }

    public DashboardCurrentDto? CurrentInfo { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
