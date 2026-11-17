using System.Text.Json;
using PanelFleet.Application.Overview;
using PanelFleet.Client.Dto;

namespace PanelFleet.Client.UnitTests.Overview;

public sealed class OverviewMapperTests
{
    private const string RealPayload = """
        {"code":200,"message":"","data":{"websiteNumber":0,"agentNumber":0,"databaseNumber":5,"cronjobNumber":0,"appInstalledNumber":4,"hostname":"node-a","os":"linux","platform":"debian","platformFamily":"debian","platformVersion":"13.7","prettyDistro":"Debian GNU/Linux 13","kernelArch":"x86_64","kernelVersion":"7.0.14-28-pve","ipV4Addr":"198.51.100.10","cpuCores":8,"cpuLogicalCores":2,"cpuModelName":"AMD Ryzen 7 2700X Eight-Core Processor","currentInfo":{"uptime":488434,"load1":0.72,"load5":1.47,"load15":1.62,"loadUsagePercent":24,"cpuUsedPercent":2.2325581395348792,"memoryTotal":2147483648,"memoryUsed":1509559296,"memoryFree":333189120,"memoryUsedPercent":70.29433250427246,"memoryAvailable":637924352,"diskData":[{"path":"/","type":"zfs","device":"rpool/data/subvol-101-disk-0","total":34359738368,"free":13631881216,"used":20727857152,"usedPercent":60.32600402832031},{"path":"/data/Routing","type":"cifs","device":"//198.51.100.1/Data","total":1967846068224,"free":104215789568,"used":1863630278656,"usedPercent":94.70406800354787}],"shotTime":"2026-11-16T06:00:21.486069412Z"}}}
        """;

    private static readonly DateTimeOffset Fallback = new(2026, 11, 16, 6, 30, 0, TimeSpan.Zero);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(
            JsonDocument.Parse(json).RootElement.GetProperty("data").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void Map_RealPayload_MapsAllFields()
    {
        var dto = Deserialize<DashboardBaseDto>(RealPayload);

        var metrics = OverviewMapper.Map(dto, Fallback);

        Assert.NotNull(metrics);
        Assert.Equal("node-a", metrics.Hostname);
        Assert.Equal("Debian GNU/Linux 13", metrics.OsDescription);
        Assert.Equal("7.0.14-28-pve", metrics.KernelVersion);
        Assert.Equal("x86_64", metrics.KernelArch);
        Assert.Equal("198.51.100.10", metrics.IpAddress);
        Assert.Equal(2, metrics.CpuCores);
        Assert.Equal(0, metrics.WebsiteCount);
        Assert.Equal(4, metrics.AppCount);
        Assert.Equal(5, metrics.DatabaseCount);
        Assert.Equal(0, metrics.CronjobCount);
        Assert.Equal(2.2325581395348792, metrics.CpuUsedPercent!.Value, 6);
        Assert.Equal(70.29433250427246, metrics.MemoryUsedPercent!.Value, 6);
        Assert.Equal(1509559296, metrics.MemoryUsedBytes);
        Assert.Equal(2147483648, metrics.MemoryTotalBytes);
        Assert.Equal(2, metrics.Disks.Count);
        Assert.Equal("/data/Routing", metrics.MaxDiskPath);
        Assert.Equal(94.70406800354787, metrics.MaxDiskUsedPercent!.Value, 6);
        Assert.Equal(20727857152L + 1863630278656L, metrics.DiskUsedBytes);
        Assert.Equal(34359738368L + 1967846068224L, metrics.DiskTotalBytes);
        Assert.Equal(488434, metrics.UptimeSeconds);
        Assert.Equal(0.72, metrics.Load1!.Value, 6);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 16, 6, 0, 21, TimeSpan.Zero).AddTicks(4860694),
            metrics.SampledAt);
    }

    [Fact]
    public void Map_MissingCurrentInfo_KeepsCountsAndHost()
    {
        var dto = Deserialize<DashboardBaseDto>("""{"code":200,"data":{"hostname":"h","websiteNumber":3}}""");

        var metrics = OverviewMapper.Map(dto, Fallback);

        Assert.NotNull(metrics);
        Assert.Equal("h", metrics.Hostname);
        Assert.Equal(3, metrics.WebsiteCount);
        Assert.Null(metrics.CpuUsedPercent);
        Assert.Empty(metrics.Disks);
        Assert.Equal(Fallback, metrics.SampledAt);
    }

    [Fact]
    public void Map_Null_ReturnsNull()
    {
        Assert.Null(OverviewMapper.Map(null, Fallback));
    }

    [Fact]
    public void Map_ClampsPercentagesAndHandlesBadValues()
    {
        var dto = new DashboardBaseDto
        {
            CurrentInfo = new DashboardCurrentDto
            {
                CpuUsedPercent = 150,
                MemoryUsedPercent = -5,
                DiskData =
                [
                    new DiskInfoDto { Path = "/", UsedPercent = 200, Used = 0, Total = 100 }
                ]
            }
        };

        var metrics = OverviewMapper.Map(dto, Fallback);

        Assert.NotNull(metrics);
        Assert.Equal(100, metrics.CpuUsedPercent);
        Assert.Equal(0, metrics.MemoryUsedPercent);
        Assert.Equal(100, metrics.MaxDiskUsedPercent);
        Assert.Null(metrics.MemoryUsedBytes);
        Assert.Null(metrics.DiskUsedBytes);
        Assert.Equal(100, metrics.DiskTotalBytes);
    }

    [Fact]
    public void Map_FallsBackToPlatformVersion_WhenPrettyDistroMissing()
    {
        var dto = new DashboardBaseDto { Platform = "debian", PlatformVersion = "13.7", Os = "linux" };

        var metrics = OverviewMapper.Map(dto, Fallback);

        Assert.Equal("debian 13.7", metrics!.OsDescription);
    }
}
