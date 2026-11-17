using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Settings;
using PanelFleet.Domain;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

public sealed class SettingsContractTests
{
    private const string SettingInfoJson = """
        {"systemVersion":"v2.3.2","panelName":"master-panel","edition":"cn","theme":"dark","language":"zh",
         "serverPort":"9999","securityEntrance":"s3cret","ssl":"Disable","sslType":"","sessionTimeout":"86400",
         "expirationDays":"0","complexityVerification":"Enable","proxyPasswd":"should-not-be-modeled"}
        """;

    private const string TerminalInfoJson = """
        {"showTerminalButton":"Enable","fontSize":"14","scrollback":"1000","cursorStyle":"block"}
        """;

    private const string CurrentUserJson = """
        {"name":"admin","mfaStatus":"Enable","mfaInterval":"300","apiInterfaceStatus":"Disable",
         "apiKey":"plaintext-key-should-not-leak","ipWhiteList":"","apiKeyValidityTime":"0"}
        """;

    private const string SslInfoJson = """
        {"domain":"panel.example.com","timeout":"2027-01-01 00:00:00","rootPath":"/opt/1panel","cert":"PEM","key":"KEY","sslID":"3"}
        """;

    [Fact]
    public void Routes_MatchOnePanelV232Paths()
    {
        Assert.Equal("api/v2/core/settings/search", SettingsRoutes.CoreSettingsSearch);
        Assert.Equal("api/v2/core/settings/search/base", SettingsRoutes.CoreSettingsSearchBase);
        Assert.Equal("api/v2/core/settings/terminal/search", SettingsRoutes.CoreTerminalSearch);
        Assert.Equal("api/v2/core/settings/update", SettingsRoutes.CoreSettingsUpdate);
        Assert.Equal("api/v2/core/settings/terminal/update", SettingsRoutes.CoreTerminalUpdate);
        Assert.Equal("api/v2/core/settings/ssl/info", SettingsRoutes.CoreSslInfo);
        Assert.Equal("api/v2/core/auth/current", SettingsRoutes.CoreCurrentUser);
        Assert.Equal("api/v2/core/auth/current/update", SettingsRoutes.CoreCurrentUserUpdate);
        Assert.Equal("api/v2/core/auth/api/update", SettingsRoutes.CoreApiConfigUpdate);
        Assert.Equal("api/v2/settings/website/dir", SettingsRoutes.AgentWebsiteDir);
    }

    [Fact]
    public async Task CoreSettingsSearch_ParsesSettingInfo()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [SettingsRoutes.CoreSettingsSearch] = SettingInfoJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.PostAsync<PanelSettingInfoDto>(SettingsRoutes.CoreSettingsSearch, new { });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("master-panel", result.Data.PanelName);
        Assert.Equal("v2.3.2", result.Data.SystemVersion);
        Assert.Equal("9999", result.Data.ServerPort);
        Assert.Equal("s3cret", result.Data.SecurityEntrance);
    }

    [Fact]
    public async Task CoreSettingsUpdate_SendsKeyValueBody()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [SettingsRoutes.CoreSettingsUpdate] = null
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.PostAsync<PanelSettingsAckDto>(
            SettingsRoutes.CoreSettingsUpdate,
            new PanelSettingUpdateRequest { Key = "PanelName", Value = "新面板名" });

        Assert.True(result.IsSuccess, result.Error?.Message);
        var body = server.GetLastRequestBody(SettingsRoutes.CoreSettingsUpdate);
        Assert.NotNull(body);
        // 断言用 JSON 解析而非原始子串：客户端序列化可能按默认编码器转义非 ASCII（\uXXXX），
        // 语义仍是正确的 JSON，解析后比较不依赖转义策略。
        using var document = JsonDocument.Parse(body);
        Assert.Equal("PanelName", document.RootElement.GetProperty("key").GetString());
        Assert.Equal("新面板名", document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task CoreTerminalSearch_ParsesTerminalInfo()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [SettingsRoutes.CoreTerminalSearch] = TerminalInfoJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.PostAsync<PanelTerminalInfoDto>(SettingsRoutes.CoreTerminalSearch, new { });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Enable", result.Data?.ShowTerminalButton);
        Assert.Equal("1000", result.Data?.Scrollback);
    }

    [Fact]
    public async Task CoreCurrentUser_ParsesApiStatus()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [SettingsRoutes.CoreCurrentUser] = CurrentUserJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.GetAsync<PanelCurrentUserDto>(SettingsRoutes.CoreCurrentUser);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("admin", result.Data?.Name);
        Assert.Equal("Disable", result.Data?.ApiInterfaceStatus);
        Assert.Equal("Enable", result.Data?.MfaStatus);
    }

    [Fact]
    public async Task CoreSslInfo_ComputesHasCertificate()
    {
        await using var server = new FakePanelServer(new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [SettingsRoutes.CoreSslInfo] = SslInfoJson
            }
        });
        await server.StartAsync();
        using var client = CreateClient(server);

        var result = await client.GetAsync<PanelSslInfoDto>(SettingsRoutes.CoreSslInfo);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("panel.example.com", result.Data?.Domain);
        Assert.Equal("2027-01-01 00:00:00", result.Data?.Timeout);
        Assert.True(result.Data?.HasCertificate);
    }

    private static IPanelClient CreateClient(FakePanelServer server) =>
        new PanelClientFactory(
                new PanelHttpOptions { RequestTimeout = TimeSpan.FromSeconds(5) },
                TimeProvider.System,
                NullLoggerFactory.Instance)
            .Create(new NodeDescriptor(
                NodeId.New(),
                "node-1",
                server.BaseUri,
                "test-api-key",
                AuthMode.HmacSha256,
                TlsPolicy.Strict));
}
