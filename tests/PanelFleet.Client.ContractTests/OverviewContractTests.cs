using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Overview;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

/// <summary>总览数据契约：用真实 HTTP 走完整客户端链路，再映射为聚合指标。</summary>
public sealed class OverviewContractTests
{
    [Fact]
    public async Task FullPayload_MapsToMetrics()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);
        var result = await client.GetDashboardBaseAsync("all", "all");

        Assert.True(result.IsSuccess);

        var metrics = OverviewMapper.Map(result.Data, DateTimeOffset.UtcNow);

        Assert.NotNull(metrics);
        Assert.Equal("node-1", metrics.Hostname);
        Assert.Equal("Debian GNU/Linux 13", metrics.OsDescription);
        Assert.Equal("198.51.100.10", metrics.IpAddress);
        Assert.Equal(2, metrics.CpuCores);
        Assert.Equal(3, metrics.WebsiteCount);
        Assert.Equal(2, metrics.AppCount);
        Assert.Equal(1, metrics.DatabaseCount);
        Assert.Equal(4, metrics.CronjobCount);
        Assert.Equal(2.23, metrics.CpuUsedPercent!.Value, 6);
        Assert.Equal(70.29, metrics.MemoryUsedPercent!.Value, 6);
        Assert.Equal(1509559296, metrics.MemoryUsedBytes);
        Assert.Equal(2147483648, metrics.MemoryTotalBytes);
        Assert.Equal(2, metrics.Disks.Count);
        Assert.Equal("/data/Routing", metrics.MaxDiskPath);
        Assert.Equal(94.7, metrics.MaxDiskUsedPercent!.Value, 6);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task MissingCurrentInfo_StillMapsCounts_AndOnlyOneRequest()
    {
        var options = new FakePanelOptions { OmitCurrentInfo = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);
        var result = await client.GetDashboardBaseAsync("all", "all");

        Assert.True(result.IsSuccess);

        var metrics = OverviewMapper.Map(result.Data, DateTimeOffset.UtcNow);

        Assert.NotNull(metrics);
        Assert.Equal(3, metrics.WebsiteCount);
        Assert.Null(metrics.CpuUsedPercent);
        Assert.Empty(metrics.Disks);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Unauthorized_ReturnsStructuredError()
    {
        var options = new FakePanelOptions { RejectAll = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);
        var result = await client.GetDashboardBaseAsync("all", "all");

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Unauthorized, result.Error?.Kind);
    }

    private static IPanelClient CreateClient(FakePanelServer server, FakePanelOptions options)
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(5) },
            TimeProvider.System,
            NullLoggerFactory.Instance);

        return factory.Create(new NodeDescriptor(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            options.ApiKey,
            AuthMode.HmacSha256));
    }
}
