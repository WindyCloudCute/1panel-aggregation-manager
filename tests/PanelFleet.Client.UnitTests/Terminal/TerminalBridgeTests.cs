using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Terminal;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalBridgeTests
{
    private static TerminalBridge CreateBridge(
        FakeTerminalBackend backend,
        TerminalLimits? limits = null,
        TerminalSessionInfo? info = null)
    {
        return new TerminalBridge(
            backend,
            TerminalTestHelpers.CreateContext(),
            info ?? TerminalTestHelpers.CreateInfo(),
            limits ?? TerminalTestHelpers.FastLimits(),
            TimeProvider.System,
            NullLogger<TerminalBridge>.Instance);
    }

    private static string OutputText(FakeTerminalChannel channel)
    {
        var outputs = channel.MessagesOfType("output");
        return string.Concat(outputs.Select(payload => Encoding.UTF8.GetString(FakeTerminalChannel.DecodeOutput(payload))));
    }

    [Fact]
    public async Task Run_ForwardsUpstreamOutputToBrowser()
    {
        var backend = new FakeTerminalBackend();
        var connection = new FakeTerminalConnection();
        backend.Enqueue(connection);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);
        var telemetry = new TerminalSessionTelemetry();

        var run = bridge.RunAsync(channel, telemetry, CancellationToken.None);
        connection.PushSession();
        connection.PushOutput("hello world");

        await TerminalTestHelpers.WaitUntilAsync(() => OutputText(channel).Contains("hello world", StringComparison.Ordinal));
        Assert.NotEmpty(channel.MessagesOfType("ready"));

        channel.PushClosed();
        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.User, outcome.Reason);
        Assert.True(telemetry.OutputBytes >= "hello world".Length);
    }

    [Fact]
    public async Task Run_ForwardsBrowserInputAndResize()
    {
        var backend = new FakeTerminalBackend();
        var connection = new FakeTerminalConnection();
        backend.Enqueue(connection);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);
        var telemetry = new TerminalSessionTelemetry();

        var run = bridge.RunAsync(channel, telemetry, CancellationToken.None);
        connection.PushSession();

        var input = Encoding.UTF8.GetBytes("ls -la\r");
        channel.PushInput(input);
        channel.PushResize(132, 43);

        await TerminalTestHelpers.WaitUntilAsync(
            () => connection.Inputs.Count == 1 && connection.Resizes.Count == 1);
        Assert.Equal(input, connection.Inputs[0]);
        Assert.Equal((132, 43), connection.Resizes[0]);

        channel.PushClosed();
        await TerminalTestHelpers.WithTimeoutAsync(run);
        Assert.Equal(1, telemetry.InputFrames);
        Assert.Equal(input.Length, telemetry.InputBytes);
    }

    [Fact]
    public async Task Run_AnswersBrowserPing()
    {
        var backend = new FakeTerminalBackend();
        backend.Enqueue(new FakeTerminalConnection());
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);

        var run = bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None);
        channel.PushPing(1700000000000);

        await TerminalTestHelpers.WaitUntilAsync(() => channel.MessagesOfType("pong").Count > 0);
        channel.PushClosed();
        await TerminalTestHelpers.WithTimeoutAsync(run);
    }

    [Fact]
    public async Task Run_IgnoresUpstreamHeartbeatAndAiNotice()
    {
        var backend = new FakeTerminalBackend();
        var connection = new FakeTerminalConnection();
        backend.Enqueue(connection);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);

        var run = bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None);
        connection.PushSession();
        connection.Push(new TerminalServerFrame(TerminalServerFrameKind.Heartbeat));
        connection.Push(new TerminalServerFrame(TerminalServerFrameKind.AiNotice));
        await Task.Delay(100);

        Assert.Empty(channel.MessagesOfType("output"));

        channel.PushClosed();
        await TerminalTestHelpers.WithTimeoutAsync(run);
    }

    [Fact]
    public async Task Run_IdleTimeout_ClosesSessionWithReason()
    {
        var backend = new FakeTerminalBackend();
        backend.Enqueue(new FakeTerminalConnection());
        var channel = new FakeTerminalChannel();
        var limits = TerminalTestHelpers.FastLimits(idle: TimeSpan.FromMilliseconds(250));
        var bridge = CreateBridge(backend, limits);

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(
            bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None));

        Assert.Equal(TerminalCloseReasons.Idle, outcome.Reason);
        Assert.NotEmpty(channel.MessagesOfType("closed"));
        Assert.Equal(1000, channel.CloseCode);
    }

    [Fact]
    public async Task Run_MaxDuration_ClosesSessionWithReason()
    {
        var backend = new FakeTerminalBackend();
        backend.Enqueue(new FakeTerminalConnection());
        var channel = new FakeTerminalChannel();
        var limits = TerminalTestHelpers.FastLimits(
            idle: TimeSpan.FromMinutes(30),
            maxDuration: TimeSpan.FromMilliseconds(300));
        var bridge = CreateBridge(backend, limits);

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(
            bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None));

        Assert.Equal(TerminalCloseReasons.MaxDuration, outcome.Reason);
        Assert.Equal(1000, channel.CloseCode);
    }

    [Fact]
    public async Task Run_InputFrameTooLarge_ClosesWithInputLimit()
    {
        var backend = new FakeTerminalBackend();
        backend.Enqueue(new FakeTerminalConnection());
        var channel = new FakeTerminalChannel();
        var limits = TerminalTestHelpers.FastLimits(maxFrameBytes: 4);
        var bridge = CreateBridge(backend, limits);

        var run = bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None);
        channel.PushInput(Encoding.UTF8.GetBytes("12345"));

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.InputLimit, outcome.Reason);
        Assert.Contains("\"code\":\"input_limit\"", channel.MessagesOfType("error").Single(), StringComparison.Ordinal);
        Assert.Equal(1011, channel.CloseCode);
    }

    [Fact]
    public async Task Run_InputRateExceeded_ClosesWithInputLimit()
    {
        var backend = new FakeTerminalBackend();
        backend.Enqueue(new FakeTerminalConnection());
        var channel = new FakeTerminalChannel();
        var limits = TerminalTestHelpers.FastLimits(maxFramesPerSecond: 5);
        var bridge = CreateBridge(backend, limits);

        var run = bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None);
        for (var index = 0; index < 12; index++)
        {
            channel.PushInput(Encoding.UTF8.GetBytes("x"));
        }

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.InputLimit, outcome.Reason);
    }

    [Fact]
    public async Task Run_RevalidateCloseCode_ReattachesWithSessionAndFlag()
    {
        var backend = new FakeTerminalBackend();
        var first = new FakeTerminalConnection("session-abc");
        backend.Enqueue(first);
        var second = new FakeTerminalConnection("session-abc");
        backend.Enqueue(second);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend, TerminalTestHelpers.FastLimits());
        var telemetry = new TerminalSessionTelemetry();

        var run = bridge.RunAsync(channel, telemetry, CancellationToken.None);
        first.PushSession("session-abc");
        await TerminalTestHelpers.WaitUntilAsync(() => channel.MessagesOfType("ready").Count > 0);

        first.PushClosed(4410);
        await TerminalTestHelpers.WaitUntilAsync(() => backend.Calls.Count == 2);
        Assert.Equal((null, false), backend.Calls[0]);
        Assert.Equal(("session-abc", true), backend.Calls[1]);

        channel.PushInput(Encoding.UTF8.GetBytes("echo ok\n"));
        second.PushOutput("ok");
        await TerminalTestHelpers.WaitUntilAsync(() => OutputText(channel).Contains("ok", StringComparison.Ordinal));

        channel.PushClosed();
        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.User, outcome.Reason);
        Assert.Equal(1, telemetry.RevalidateCount);
    }

    [Fact]
    public async Task Run_RevalidateFailure_ClosesWithRevalidateFailed()
    {
        var backend = new FakeTerminalBackend();
        var first = new FakeTerminalConnection("session-abc");
        backend.Enqueue(first);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend, TerminalTestHelpers.FastLimits());
        var telemetry = new TerminalSessionTelemetry();

        var run = bridge.RunAsync(channel, telemetry, CancellationToken.None);
        first.PushSession("session-abc");
        await TerminalTestHelpers.WaitUntilAsync(() => channel.MessagesOfType("ready").Count > 0);

        backend.Failure = new TerminalUpstreamException("upstream_auth_failed", "鉴权失败");
        first.PushClosed(4410);

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.RevalidateFailed, outcome.Reason);
        Assert.Contains("\"code\":\"revalidate_failed\"", channel.MessagesOfType("error").Single(), StringComparison.Ordinal);
        Assert.Equal(0, telemetry.RevalidateCount);
        Assert.Equal(1011, channel.CloseCode);
    }

    [Fact]
    public async Task Run_UpstreamNormalClose_ProducesUpstreamClosed()
    {
        var backend = new FakeTerminalBackend();
        var connection = new FakeTerminalConnection();
        backend.Enqueue(connection);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);

        var run = bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None);
        connection.PushClosed(1000);

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.UpstreamClosed, outcome.Reason);
        Assert.True(outcome.CanReconnect);
        Assert.Equal(1000, channel.CloseCode);
    }

    [Fact]
    public async Task Run_UpstreamSessionNotFound_ReportsExpiredSession()
    {
        var backend = new FakeTerminalBackend();
        var connection = new FakeTerminalConnection();
        backend.Enqueue(connection);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);

        var run = bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None);
        connection.PushClosed(4404);

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.UpstreamClosed, outcome.Reason);
        Assert.Contains("过期", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_BrowserClose_ClosesUpstreamCleanly()
    {
        var backend = new FakeTerminalBackend();
        var connection = new FakeTerminalConnection();
        backend.Enqueue(connection);
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);

        var run = bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None);
        channel.PushClosed();

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(run);

        Assert.Equal(TerminalCloseReasons.User, outcome.Reason);
        Assert.True(connection.Closed);
    }

    [Fact]
    public async Task Run_ConnectFailure_SendsErrorAndClosesBrowser()
    {
        var backend = new FakeTerminalBackend
        {
            Failure = new TerminalUpstreamException("upstream_auth_failed", "1Panel 终端鉴权失败。")
        };
        var channel = new FakeTerminalChannel();
        var bridge = CreateBridge(backend);

        var outcome = await TerminalTestHelpers.WithTimeoutAsync(
            bridge.RunAsync(channel, new TerminalSessionTelemetry(), CancellationToken.None));

        Assert.Equal(TerminalCloseReasons.UpstreamError, outcome.Reason);
        Assert.Contains("\"code\":\"upstream_auth_failed\"", channel.MessagesOfType("error").Single(), StringComparison.Ordinal);
        Assert.Equal(1011, channel.CloseCode);
    }
}
