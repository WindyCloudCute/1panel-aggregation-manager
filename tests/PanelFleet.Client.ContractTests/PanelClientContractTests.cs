using Microsoft.Extensions.Logging;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

public sealed class PanelClientContractTests
{
    [Fact]
    public async Task HmacOnly_Succeeds()
    {
        var options = new FakePanelOptions { AcceptMd5 = false };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Ubuntu", result.Data?.Os);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task CapabilityProbe_ReturnsPanelVersion()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);
        var probe = new CapabilityProbe();

        var result = await probe.ProbeAsync(client);

        Assert.True(result.Authorized);
        Assert.Equal("v2.0.15", result.PanelVersion);
    }

    [Fact]
    public async Task Md5Only_FallsBackAndSucceeds()
    {
        var options = new FakePanelOptions { AcceptHmac = false };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task ClockSkew_IsCorrectedByDateHeader()
    {
        var options = new FakePanelOptions { RejectFirstRequest = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task WrongApiKey_ReturnsUnauthorized()
    {
        var options = new FakePanelOptions { RejectAll = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Unauthorized, result.Error?.Kind);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task BusinessError_ReturnsPanelServerError()
    {
        var options = new FakePanelOptions { BusinessErrorCode = 500 };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.PanelServerError, result.Error?.Kind);
        Assert.True(result.Error?.Message.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GzipResponse_IsDecoded()
    {
        var options = new FakePanelOptions { UseGzip = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Ubuntu", result.Data?.Os);
    }

    [Fact]
    public async Task Redirect_IsMappedToProtocolError()
    {
        var options = new FakePanelOptions { Redirect = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Protocol, result.Error?.Kind);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task MalformedJson_IsMappedToProtocolError()
    {
        var options = new FakePanelOptions { MalformedJson = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Protocol, result.Error?.Kind);
    }

    [Fact]
    public async Task HtmlResponse_IsMappedToProtocolErrorWithHint()
    {
        var options = new FakePanelOptions { ReturnHtml = true };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetOsInfoAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Protocol, result.Error?.Kind);
        Assert.True(result.Error?.Message.Contains("安全入口", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowResponse_IsMappedToTimeout()
    {
        var options = new FakePanelOptions { Delay = TimeSpan.FromSeconds(2) };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(
            server,
            options,
            new PanelHttpOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(300),
                ConnectTimeout = TimeSpan.FromSeconds(1)
            });

        var result = await client.GetOsInfoAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Timeout, result.Error?.Kind);
    }

    [Fact]
    public async Task CanceledToken_IsMappedToCanceled()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await client.GetOsInfoAsync(cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Canceled, result.Error?.Kind);
    }

    [Fact]
    public async Task UnreachableNode_IsMappedToNetworkError()
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(1),
                ConnectTimeout = TimeSpan.FromSeconds(1)
            },
            primaryHandlerFactory: _ => new ThrowingHandler());

        var client = factory.Create(
            new NodeDescriptor(NodeId.New(), "node-1", new Uri("https://unreachable.test"), "test-api-key"));

        var result = await client.GetOsInfoAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(PanelErrorKind.Network, result.Error?.Kind);
    }

    [Fact]
    public async Task DashboardBase_ParsesNestedCurrentInfo()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);

        var result = await client.GetDashboardBaseAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("node-1", result.Data?.Hostname);
        Assert.Equal("Debian GNU/Linux 13", result.Data?.PrettyDistro);
        Assert.Equal("198.51.100.10", result.Data?.IpV4Addr);
        Assert.Equal(3, result.Data?.WebsiteNumber);
        Assert.Equal(2.23, result.Data?.CurrentInfo?.CpuUsedPercent);
        Assert.Equal(2, result.Data?.CurrentInfo?.DiskData?.Count);
    }

    [Fact]
    public async Task CapabilityProbe_ReportsAuthorized()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var client = CreateClient(server, options);
        var probe = new CapabilityProbe();

        var result = await probe.ProbeAsync(client);

        Assert.True(result.Reachable);
        Assert.True(result.Authorized);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Logs_DoNotContainApiKeyOrToken()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();

        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(
            builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));

        var client = CreateClient(server, options, loggerFactory: loggerFactory);

        var result = await client.GetOsInfoAsync();

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(provider.Messages, message => message.Contains(options.ApiKey, StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Messages, message => message.Contains("1Panel-Token", StringComparison.OrdinalIgnoreCase));
    }

    private static IPanelClient CreateClient(
        FakePanelServer server,
        FakePanelOptions options,
        PanelHttpOptions? httpOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        var factory = new PanelClientFactory(
            httpOptions ?? new PanelHttpOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(2)
            },
            TimeProvider.System,
            loggerFactory);

        return factory.Create(
            new NodeDescriptor(NodeId.New(), "node-1", server.BaseUri, options.ApiKey));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
