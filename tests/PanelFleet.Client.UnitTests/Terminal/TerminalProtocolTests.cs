using System.Text;
using PanelFleet.Application.Terminal;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalProtocolTests
{
    [Fact]
    public void ParseServerFrame_Session_ReturnsSessionId()
    {
        var frame = TerminalProtocol.ParseServerFrame("{\"type\":\"session\",\"id\":\"abc-123\"}");

        Assert.Equal(TerminalServerFrameKind.Session, frame.Kind);
        Assert.Equal("abc-123", frame.SessionId);
    }

    [Fact]
    public void ParseServerFrame_Cmd_DecodesBase64Output()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello\r\n"));

        var frame = TerminalProtocol.ParseServerFrame("{\"type\":\"cmd\",\"data\":\"" + payload + "\"}");

        Assert.Equal(TerminalServerFrameKind.Output, frame.Kind);
        Assert.Equal("hello\r\n", Encoding.UTF8.GetString(frame.Data!));
    }

    [Fact]
    public void ParseServerFrame_Heartbeat_IsRecognized()
    {
        var frame = TerminalProtocol.ParseServerFrame("{\"type\":\"heartbeat\",\"timestamp\":123}");

        Assert.Equal(TerminalServerFrameKind.Heartbeat, frame.Kind);
    }

    [Fact]
    public void ParseServerFrame_AiNotice_IsRecognized()
    {
        var frame = TerminalProtocol.ParseServerFrame("{\"type\":\"ai_notice\",\"level\":\"info\",\"message\":\"x\"}");

        Assert.Equal(TerminalServerFrameKind.AiNotice, frame.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"unknown\"}")]
    public void ParseServerFrame_Malformed_ReturnsUnknown(string payload)
    {
        var frame = TerminalProtocol.ParseServerFrame(payload);

        Assert.Equal(TerminalServerFrameKind.Unknown, frame.Kind);
    }

    [Fact]
    public void ParseServerFrame_InvalidBase64_ReturnsEmptyOutput()
    {
        var frame = TerminalProtocol.ParseServerFrame("{\"type\":\"cmd\",\"data\":\"%%%\"}");

        Assert.Equal(TerminalServerFrameKind.Output, frame.Kind);
        Assert.Empty(frame.Data!);
    }

    [Fact]
    public void EncodeInput_Produces1PanelCmdFrame()
    {
        var data = Encoding.UTF8.GetBytes("ls -la\r");

        var payload = TerminalProtocol.EncodeInput(data);

        using var document = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal("cmd", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            data,
            Convert.FromBase64String(document.RootElement.GetProperty("data").GetString()!));
    }

    [Fact]
    public void BrowserInputFrame_ParsesToInput()
    {
        var data = Encoding.UTF8.GetBytes("ls -la\r");
        var payload = "{\"type\":\"input\",\"data\":\"" + Convert.ToBase64String(data) + "\"}";

        Assert.True(TerminalProtocol.TryParseBrowserFrame(payload, out var frame));
        Assert.Equal(TerminalBrowserFrameKind.Input, frame.Kind);
        Assert.Equal(data, frame.Data!);
    }

    [Fact]
    public void EncodeResize_ParsesAsResize()
    {
        var payload = TerminalProtocol.EncodeResize(120, 40);

        Assert.True(TerminalProtocol.TryParseBrowserFrame(payload, out var frame));
        Assert.Equal(TerminalBrowserFrameKind.Resize, frame.Kind);
        Assert.Equal(120, frame.Cols);
        Assert.Equal(40, frame.Rows);
    }

    [Fact]
    public void BrowserPing_Parses()
    {
        Assert.True(TerminalProtocol.TryParseBrowserFrame("{\"type\":\"ping\",\"timestamp\":42}", out var frame));
        Assert.Equal(TerminalBrowserFrameKind.Ping, frame.Kind);
        Assert.Equal(42, frame.Timestamp);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"input\"}")]
    [InlineData("{\"type\":\"input\",\"data\":\"%%%\"}")]
    [InlineData("{\"type\":\"resize\",\"cols\":0,\"rows\":10}")]
    [InlineData("{\"type\":\"other\"}")]
    public void TryParseBrowserFrame_RejectsMalformed(string payload)
    {
        Assert.False(TerminalProtocol.TryParseBrowserFrame(payload, out _));
    }

    [Fact]
    public void EncodeOutput_IsBrowserOutputFrame()
    {
        var payload = TerminalProtocol.EncodeOutput(Encoding.UTF8.GetBytes("x"));

        Assert.Contains("\"type\":\"output\"", payload, StringComparison.Ordinal);
        Assert.Equal("x", Encoding.UTF8.GetString(Convert.FromBase64String(
            System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("data").GetString()!)));
    }

    [Fact]
    public void EncodeReady_ContainsSessionAndMode()
    {
        var payload = TerminalProtocol.EncodeReady(
            "s1",
            TerminalMode.Container,
            Guid.NewGuid().ToString("D"),
            "节点 A",
            "web-1",
            100,
            30);

        using var document = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal("ready", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("container", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal("web-1", document.RootElement.GetProperty("container").GetString());
        Assert.Equal(100, document.RootElement.GetProperty("cols").GetInt32());
    }

    [Fact]
    public void EncodeErrorAndClosed_HaveStableShape()
    {
        using var error = System.Text.Json.JsonDocument.Parse(
            TerminalProtocol.EncodeError("input_limit", "过于频繁"));
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("input_limit", error.RootElement.GetProperty("code").GetString());

        using var closed = System.Text.Json.JsonDocument.Parse(
            TerminalProtocol.EncodeClosed("idle", "空闲超时", canReconnect: true));
        Assert.Equal("closed", closed.RootElement.GetProperty("type").GetString());
        Assert.True(closed.RootElement.GetProperty("canReconnect").GetBoolean());
    }

    [Fact]
    public void DecodeBase64_Invalid_ReturnsNull()
    {
        Assert.Null(TerminalProtocol.DecodeBase64("%%%"));
        Assert.Empty(TerminalProtocol.DecodeBase64(string.Empty)!);
        Assert.Equal("abc", Encoding.UTF8.GetString(TerminalProtocol.DecodeBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes("abc")))!));
    }
}
