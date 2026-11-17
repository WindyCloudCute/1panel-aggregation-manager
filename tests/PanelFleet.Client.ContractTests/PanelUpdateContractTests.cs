using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Updates;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P12 面板更新契约测试：用真实 <see cref="PanelClientFactory"/> + 真实 HTTP 打到假面板，
/// 验证 core 路由（/api/v2/core）、只读版本查询、可用版本/更新说明与升级提交参数。
/// </summary>
public sealed class PanelUpdateContractTests
{
    [Fact]
    public async Task UpgradeInfo_ReturnsAvailableVersionAndReleaseNote()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [UpdateRoutes.UpgradeInfo] =
                    """{"testVersion":"","newVersion":"","latestVersion":"v2.5.1","releaseNote":"## 更新内容\n\n- 修复若干问题"}"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var result = await client.GetAsync<PanelUpgradeInfoDto>(UpdateRoutes.UpgradeInfo);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        Assert.Equal(string.Empty, result.Data!.TestVersion);
        Assert.Equal(string.Empty, result.Data.NewVersion);
        Assert.Equal("v2.5.1", result.Data.LatestVersion);
        Assert.Contains("更新内容", result.Data.ReleaseNote, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task PanelSettingBase_ReadOnlyPostQuery_ReturnsSystemVersion()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [UpdateRoutes.PanelSettingBase] = """{"systemVersion":"v2.3.2","edition":"community"}"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var result = await client.PostAsync<PanelSettingBaseDto>(UpdateRoutes.PanelSettingBase, new { });

        Assert.True(result.IsSuccess);
        Assert.Equal("v2.3.2", result.Data?.SystemVersion);
        Assert.Equal("community", result.Data?.Edition);

        var body = server.GetLastRequestBody(UpdateRoutes.PanelSettingBase);
        Assert.NotNull(body);
        Assert.Contains("{}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradeExecute_PostsTargetVersionToCoreRoute()
    {
        var options = new FakePanelOptions
        {
            // 升级接口返回 data:null（异步执行，无任务 ID）。
            JsonRoutes = new Dictionary<string, string?> { [UpdateRoutes.UpgradeExecute] = null }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var result = await client.PostAsync<PanelUpdateAckDto>(UpdateRoutes.UpgradeExecute, new { version = "v2.5.1" });

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);

        var body = server.GetLastRequestBody(UpdateRoutes.UpgradeExecute);
        Assert.NotNull(body);
        Assert.Contains("\"version\":\"v2.5.1\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradeInfo_EmptyVersions_MeansAlreadyLatest()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [UpdateRoutes.UpgradeInfo] =
                    """{"testVersion":"","newVersion":"","latestVersion":"","releaseNote":""}"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server);

        var result = await client.GetAsync<PanelUpgradeInfoDto>(UpdateRoutes.UpgradeInfo);

        Assert.True(result.IsSuccess);
        Assert.True(string.IsNullOrEmpty(result.Data?.NewVersion));
        Assert.True(string.IsNullOrEmpty(result.Data?.LatestVersion));
        Assert.True(string.IsNullOrEmpty(result.Data?.ReleaseNote));
    }

    [Fact]
    public void Routes_KeepCorePrefix_AndMatchProbedEndpoints()
    {
        Assert.Equal("api/v2/core/settings/search/base", UpdateRoutes.PanelSettingBase);
        Assert.Equal("api/v2/core/settings/upgrade", UpdateRoutes.UpgradeInfo);
        Assert.Equal("api/v2/core/settings/upgrade", UpdateRoutes.UpgradeExecute);
        Assert.Equal("api/v2/dashboard/base/all/all", UpdateRoutes.HealthProbe);
    }

    private static IPanelClient CreateClient(FakePanelServer server)
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(5) },
            TimeProvider.System,
            NullLoggerFactory.Instance);

        return factory.Create(new NodeDescriptor(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            "test-api-key",
            AuthMode.HmacSha256,
            TlsPolicy.Strict));
    }
}
