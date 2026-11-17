using PanelFleet.Application.Models;
using PanelFleet.Client.Dto;

namespace PanelFleet.Application.Overview;

/// <summary>把 1Panel 的 dashboard/base/all/all 报文映射为聚合指标（纯函数，可单测）。</summary>
public static class OverviewMapper
{
    public static NodeMetrics? Map(DashboardBaseDto? dto, DateTimeOffset sampledAtFallback)
    {
        if (dto is null)
        {
            return null;
        }

        var current = dto.CurrentInfo;
        var disks = MapDisks(current);
        var maxDisk = disks
            .Where(disk => disk.UsedPercent is not null)
            .OrderByDescending(disk => disk.UsedPercent!.Value)
            .FirstOrDefault();

        var diskUsed = disks.Any(disk => disk.UsedBytes is not null)
            ? disks.Sum(disk => disk.UsedBytes ?? 0)
            : (long?)null;
        var diskTotal = disks.Any(disk => disk.TotalBytes is not null)
            ? disks.Sum(disk => disk.TotalBytes ?? 0)
            : (long?)null;

        return new NodeMetrics(
            Hostname: NullIfBlank(dto.Hostname),
            OsDescription: BuildOsDescription(dto),
            KernelVersion: NullIfBlank(dto.KernelVersion),
            KernelArch: NullIfBlank(dto.KernelArch),
            IpAddress: NullIfBlank(dto.IpV4Addr),
            CpuModelName: NullIfBlank(dto.CpuModelName),
            CpuCores: dto.CpuLogicalCores > 0 ? dto.CpuLogicalCores : dto.CpuCores > 0 ? dto.CpuCores : null,
            WebsiteCount: dto.WebsiteNumber,
            AppCount: dto.AppInstalledNumber,
            DatabaseCount: dto.DatabaseNumber,
            CronjobCount: dto.CronjobNumber,
            CpuUsedPercent: ClampPercent(current?.CpuUsedPercent),
            MemoryUsedPercent: ClampPercent(current?.MemoryUsedPercent),
            MemoryUsedBytes: PositiveOrNull(current?.MemoryUsed),
            MemoryTotalBytes: PositiveOrNull(current?.MemoryTotal),
            MaxDiskUsedPercent: ClampPercent(maxDisk?.UsedPercent),
            MaxDiskPath: maxDisk?.Path,
            DiskUsedBytes: diskUsed,
            DiskTotalBytes: diskTotal,
            Disks: disks,
            Load1: FiniteOrNull(current?.Load1),
            Load5: FiniteOrNull(current?.Load5),
            Load15: FiniteOrNull(current?.Load15),
            UptimeSeconds: PositiveOrNull(current?.Uptime),
            SampledAt: current?.ShotTime ?? sampledAtFallback);
    }

    private static IReadOnlyList<DiskUsage> MapDisks(DashboardCurrentDto? current)
    {
        if (current?.DiskData is not { Count: > 0 } source)
        {
            return [];
        }

        return source
            .Select(disk => new DiskUsage(
                Path: NullIfBlank(disk.Path) ?? NullIfBlank(disk.Device) ?? "?",
                TotalBytes: PositiveOrNull(disk.Total),
                UsedBytes: disk.Used > 0 ? disk.Used : null,
                UsedPercent: ClampPercent(disk.UsedPercent)))
            .ToArray();
    }

    private static string? BuildOsDescription(DashboardBaseDto dto)
    {
        var pretty = NullIfBlank(dto.PrettyDistro);
        if (pretty is not null)
        {
            return pretty;
        }

        var platform = NullIfBlank(dto.Platform);
        var version = NullIfBlank(dto.PlatformVersion);
        if (platform is not null && version is not null)
        {
            return platform + " " + version;
        }

        return platform ?? NullIfBlank(dto.Os);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? ClampPercent(double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0d, 100d);
    }

    private static double? FiniteOrNull(double? value) =>
        value is null || !double.IsFinite(value.Value) ? null : value;

    private static long? PositiveOrNull(long? value) =>
        value is > 0 ? value : null;
}
