using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Overview;

internal static class TestNodes
{
    public static NodeRecord Record(
        string name = "node-1",
        bool enabled = true,
        NodeHealth health = NodeHealth.Online,
        Guid? groupId = null,
        string? securityEntrance = null,
        Uri? baseUri = null,
        string? panelVersion = "v2.3.2") => new(
        Id: NodeId.New(),
        Name: name,
        BaseUrl: baseUri ?? new Uri("https://panel.example.test/"),
        SecurityEntrance: securityEntrance,
        ApiKeyProtected: "cipher",
        ApiKeyHint: "1234",
        AuthMode: AuthMode.Auto,
        DetectedAuthMode: null,
        ClockOffsetSeconds: null,
        TlsPolicy: TlsPolicy.Strict,
        GroupId: groupId,
        Tags: [],
        Remark: null,
        IsEnabled: enabled,
        PanelVersion: panelVersion,
        Capabilities: PanelCapability.Dashboard,
        Health: health,
        HealthMessage: null,
        LastCheckedAt: new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero),
        LastSeenAt: null,
        CreatedAt: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

    public static NodeMetrics Metrics(
        DateTimeOffset sampledAt,
        double? cpu = 10,
        double? memoryPercent = 50,
        long? memoryUsed = 100,
        long? memoryTotal = 200,
        double? maxDiskPercent = 50,
        string? maxDiskPath = "/",
        long? diskUsed = 50,
        long? diskTotal = 100,
        int? websites = 2,
        int? apps = 1,
        int? databases = 1,
        int? cronjobs = 1) => new(
        Hostname: "host-1",
        OsDescription: "Debian GNU/Linux 13",
        KernelVersion: "7.0.14",
        KernelArch: "x86_64",
        IpAddress: "198.51.100.10",
        CpuModelName: "AMD Ryzen",
        CpuCores: 2,
        WebsiteCount: websites,
        AppCount: apps,
        DatabaseCount: databases,
        CronjobCount: cronjobs,
        CpuUsedPercent: cpu,
        MemoryUsedPercent: memoryPercent,
        MemoryUsedBytes: memoryUsed,
        MemoryTotalBytes: memoryTotal,
        MaxDiskUsedPercent: maxDiskPercent,
        MaxDiskPath: maxDiskPath,
        DiskUsedBytes: diskUsed,
        DiskTotalBytes: diskTotal,
        Disks: [],
        Load1: 0.5,
        Load5: 0.7,
        Load15: 0.9,
        UptimeSeconds: 3600,
        SampledAt: sampledAt);

    public static NodeOverviewData Data(
        DateTimeOffset attemptedAt,
        NodeMetrics? metrics = null,
        PanelError? error = null,
        DateTimeOffset? fetchedAt = null) => new(
        Metrics: metrics,
        Error: error,
        FetchedAt: fetchedAt ?? (error is null ? attemptedAt : null),
        AttemptedAt: attemptedAt,
        LastLatency: TimeSpan.FromMilliseconds(10),
        LastAttemptSucceeded: error is null);
}
