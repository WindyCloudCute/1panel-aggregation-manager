using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Terminal;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalSessionManagerTests
{
    private sealed record Fixture(
        TerminalSessionManager Manager,
        FakeNodeStore Nodes,
        NodeRecord Node,
        FakePanelClient Client,
        FakeTerminalBackend Backend,
        FakeAuditStore Audit,
        TerminalSessionRegistry Registry);

    private static Fixture Create(Action<TerminalOptions>? configure = null, bool enabledNode = true)
    {
        var options = new TerminalOptions();
        configure?.Invoke(options);

        var nodes = new FakeNodeStore();
        var node = FakeNodeStore.CreateNode(enabled: enabledNode);
        nodes.Nodes.Add(node);

        var client = new FakePanelClient(new NodeDescriptor(
            node.Id,
            node.Name,
            node.BaseUrl,
            FakeSecretProtector.ApiKey));
        var clientFactory = new FakePanelClientFactory { Client = client };
        var backend = new FakeTerminalBackend();
        var audit = new FakeAuditStore();
        var registry = new TerminalSessionRegistry(options);
        var manager = new TerminalSessionManager(
            nodes,
            new FakeSecretProtector(),
            clientFactory,
            audit,
            backend,
            registry,
            options,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<TerminalSessionManager>.Instance);

        return new Fixture(manager, nodes, node, client, backend, audit, registry);
    }

    private static TerminalSessionRequest HostRequest(NodeId nodeId) =>
        new(nodeId, TerminalMode.Host, null, null, null, 100, 30);

    private static TerminalSessionRequest ContainerRequest(NodeId nodeId, string container = "1Panel-uptime-kuma") =>
        new(nodeId, TerminalMode.Container, container, "/bin/sh", "root", 100, 30);

    private static void ConfigureSsh(Fixture fixture, string? addr = "127.0.0.1") =>
        fixture.Client.OnGet(TerminalRoutes.LocalSshConnection, new TerminalLocalSshConnectionDto
        {
            Addr = addr,
            Port = 22,
            User = "root",
            LocalSSHConnShow = "Enable"
        });

    private static void ConfigureContainers(Fixture fixture, params ContainerItemDto[] items) =>
        fixture.Client.OnPost(TerminalRoutes.ContainerSearch, new PagedItemsDto<ContainerItemDto>
        {
            Total = items.Length,
            Items = items.ToList()
        });

    [Fact]
    public async Task Open_WhenDisabled_Throws()
    {
        var fixture = Create(options => options.Enabled = false);

        var exception = await Assert.ThrowsAsync<TerminalDisabledException>(
            () => fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None));

        Assert.Equal("terminal_disabled", exception.Code);
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task Open_WhenNodeMissing_Throws()
    {
        var fixture = Create();

        var exception = await Assert.ThrowsAsync<TerminalNotFoundException>(
            () => fixture.Manager.OpenAsync(HostRequest(NodeId.New()), ActorInfo.System, CancellationToken.None));

        Assert.Equal("node_not_found", exception.Code);
    }

    [Fact]
    public async Task Open_WhenNodeDisabled_Throws()
    {
        var fixture = Create(enabledNode: false);

        var exception = await Assert.ThrowsAsync<TerminalNotFoundException>(
            () => fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None));

        Assert.Equal("node_disabled", exception.Code);
    }

    [Fact]
    public async Task Open_WhenLocalSshMissing_ReturnsReadableError()
    {
        var fixture = Create();
        ConfigureSsh(fixture, addr: null);

        var exception = await Assert.ThrowsAsync<TerminalValidationException>(
            () => fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None));

        Assert.Equal("host_ssh_not_configured", exception.Code);
        Assert.Contains("SSH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_WhenPreflightUnauthorized_ThrowsUpstreamAuthError()
    {
        var fixture = Create();
        fixture.Client.OnGetError(TerminalRoutes.LocalSshConnection, PanelErrorKind.Unauthorized);

        var exception = await Assert.ThrowsAsync<TerminalUpstreamException>(
            () => fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None));

        Assert.Equal("upstream_auth_failed", exception.Code);
    }

    [Fact]
    public async Task Open_Host_RegistersSessionAndWritesStartAudit()
    {
        var fixture = Create();
        ConfigureSsh(fixture);

        var handle = await fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None);

        Assert.Equal(TerminalMode.Host, handle.Info.Mode);
        Assert.Equal(fixture.Node.Id, handle.Info.NodeId);
        Assert.Equal(1, fixture.Registry.Count);
        Assert.Single(fixture.Audit.Entries);
        Assert.Equal("terminal.session.start", fixture.Audit.Entries[0].Action);
        Assert.Equal(fixture.Node.Id, fixture.Audit.Entries[0].NodeId);
    }

    [Fact]
    public async Task Open_Container_WhenNotFound_Throws()
    {
        var fixture = Create();
        ConfigureContainers(fixture);

        var exception = await Assert.ThrowsAsync<TerminalNotFoundException>(
            () => fixture.Manager.OpenAsync(ContainerRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None));

        Assert.Equal("container_not_found", exception.Code);
    }

    [Fact]
    public async Task Open_Container_WhenNotRunning_Throws()
    {
        var fixture = Create();
        ConfigureContainers(fixture, new ContainerItemDto
        {
            ContainerID = "abc",
            Name = "1Panel-uptime-kuma",
            State = "exited"
        });

        var exception = await Assert.ThrowsAsync<TerminalValidationException>(
            () => fixture.Manager.OpenAsync(ContainerRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None));

        Assert.Equal("container_not_running", exception.Code);
    }

    [Fact]
    public async Task Open_Container_SucceedsWhenRunning()
    {
        var fixture = Create();
        ConfigureContainers(fixture, new ContainerItemDto
        {
            ContainerID = "abc",
            Name = "1Panel-uptime-kuma",
            State = "running"
        });

        var handle = await fixture.Manager.OpenAsync(
            ContainerRequest(fixture.Node.Id),
            ActorInfo.System,
            CancellationToken.None);

        Assert.Equal(TerminalMode.Container, handle.Info.Mode);
        Assert.Equal("1Panel-uptime-kuma", handle.Info.Container);
    }

    [Fact]
    public async Task Open_Container_RejectsIllegalCommand()
    {
        var fixture = Create();
        var request = ContainerRequest(fixture.Node.Id) with { ContainerCommand = "sh; rm -rf /" };

        var exception = await Assert.ThrowsAsync<TerminalValidationException>(
            () => fixture.Manager.OpenAsync(request, ActorInfo.System, CancellationToken.None));

        Assert.Equal("bad_request", exception.Code);
    }

    [Fact]
    public async Task Open_WhenCapacityReached_Throws()
    {
        var fixture = Create(options => options.MaxConcurrentSessions = 1);
        ConfigureSsh(fixture);

        await fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TerminalCapacityException>(
            () => fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None));

        Assert.Equal("capacity", exception.Code);
        Assert.Equal(1, fixture.Registry.Count);
    }

    [Fact]
    public async Task Run_WritesEndAuditAndUnregistersSession()
    {
        var fixture = Create();
        ConfigureSsh(fixture);
        var handle = await fixture.Manager.OpenAsync(HostRequest(fixture.Node.Id), ActorInfo.System, CancellationToken.None);

        fixture.Backend.Enqueue(new FakeTerminalConnection());
        var channel = new FakeTerminalChannel();
        channel.PushClosed();

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(
            handle.RunAsync(channel, CancellationToken.None));

        Assert.Equal(TerminalCloseReasons.User, outcome.Reason);
        Assert.Equal(0, fixture.Registry.Count);
        Assert.Equal(2, fixture.Audit.Entries.Count);
        Assert.Equal("terminal.session.start", fixture.Audit.Entries[0].Action);
        var end = fixture.Audit.Entries[1];
        Assert.Equal("terminal.session.end", end.Action);
        Assert.Equal(fixture.Node.Id, end.NodeId);
        Assert.NotNull(end.DetailJson);
        using var detail = JsonDocument.Parse(end.DetailJson!);
        Assert.Equal("user", detail.RootElement.GetProperty("closeReason").GetString());
        Assert.True(detail.RootElement.GetProperty("durationSeconds").GetDouble() >= 0);
        Assert.False(detail.RootElement.TryGetProperty("input", out _));
        Assert.False(detail.RootElement.TryGetProperty("output", out _));
    }
}
