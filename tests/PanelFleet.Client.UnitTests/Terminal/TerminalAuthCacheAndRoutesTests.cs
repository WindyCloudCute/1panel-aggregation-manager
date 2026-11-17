using PanelFleet.Application.Terminal;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalAuthCacheTests
{
    [Fact]
    public void Remember_ThenTryGet_ReturnsMode()
    {
        var cache = new TerminalAuthCache();
        var nodeId = NodeId.New();

        cache.Remember(nodeId, AuthMode.Md5);

        Assert.Equal(AuthMode.Md5, cache.TryGet(nodeId));
    }

    [Fact]
    public void AutoMode_IsNotCached()
    {
        var cache = new TerminalAuthCache();
        var nodeId = NodeId.New();

        cache.Remember(nodeId, AuthMode.Auto);

        Assert.Null(cache.TryGet(nodeId));
    }

    [Fact]
    public void Forget_RemovesEntry()
    {
        var cache = new TerminalAuthCache();
        var nodeId = NodeId.New();
        cache.Remember(nodeId, AuthMode.HmacSha256);

        cache.Forget(nodeId);

        Assert.Null(cache.TryGet(nodeId));
    }

    [Fact]
    public void Nodes_AreIsolated()
    {
        var cache = new TerminalAuthCache();
        var first = NodeId.New();
        var second = NodeId.New();

        cache.Remember(first, AuthMode.Md5);
        cache.Remember(second, AuthMode.HmacSha256);

        Assert.Equal(AuthMode.Md5, cache.TryGet(first));
        Assert.Equal(AuthMode.HmacSha256, cache.TryGet(second));
    }
}

public sealed class TerminalRoutesTests
{
    private static TerminalConnectContext Context(
        TerminalMode mode,
        string baseUrl = "http://198.51.100.10:1000",
        string? container = null)
    {
        return new TerminalConnectContext(
            new NodeDescriptor(NodeId.New(), "node-a", new Uri(baseUrl), "key"),
            mode,
            container,
            mode == TerminalMode.Container ? "/bin/sh" : null,
            mode == TerminalMode.Container ? "root" : null,
            100,
            30);
    }

    [Fact]
    public void BuildTerminalUri_Host_UsesLocalPathAndWsScheme()
    {
        var uri = TerminalRoutes.BuildTerminalUri(Context(TerminalMode.Host), sessionId: null, revalidate: false);

        Assert.Equal("ws", uri.Scheme);
        Assert.Equal("/api/v2/hosts/terminal/local", uri.AbsolutePath);
        Assert.Contains("cols=100", uri.Query, StringComparison.Ordinal);
        Assert.Contains("rows=30", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("terminalRevalidate", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTerminalUri_Container_IncludesExecParameters()
    {
        var uri = TerminalRoutes.BuildTerminalUri(
            Context(TerminalMode.Container, container: "1Panel-uptime-kuma"),
            sessionId: null,
            revalidate: false);

        Assert.Equal("/api/v2/hosts/terminal/container", uri.AbsolutePath);
        Assert.Contains("source=container", uri.Query, StringComparison.Ordinal);
        Assert.Contains("containerid=1Panel-uptime-kuma", uri.Query, StringComparison.Ordinal);
        Assert.Contains("command=%2Fbin%2Fsh", uri.Query, StringComparison.Ordinal);
        Assert.Contains("user=root", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTerminalUri_Revalidate_AppendsSessionAndFlag()
    {
        var uri = TerminalRoutes.BuildTerminalUri(
            Context(TerminalMode.Host),
            sessionId: "sess-1",
            revalidate: true);

        Assert.Contains("session=sess-1", uri.Query, StringComparison.Ordinal);
        Assert.Contains("terminalRevalidate=1", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTerminalUri_Https_UsesWss()
    {
        var uri = TerminalRoutes.BuildTerminalUri(
            Context(TerminalMode.Host, baseUrl: "https://panel.example.com"),
            sessionId: null,
            revalidate: false);

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal(443, uri.Port);
    }

    [Fact]
    public void BuildTerminalUri_KeepsReverseProxyBasePath()
    {
        var uri = TerminalRoutes.BuildTerminalUri(
            Context(TerminalMode.Host, baseUrl: "http://panel.example.com/1panel"),
            sessionId: null,
            revalidate: false);

        Assert.Equal("/1panel/api/v2/hosts/terminal/local", uri.AbsolutePath);
    }
}
