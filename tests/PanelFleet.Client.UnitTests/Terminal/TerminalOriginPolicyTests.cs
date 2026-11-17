using PanelFleet.Application.Terminal;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalOriginPolicyTests
{
    private static readonly string[] NoOrigins = [];

    [Fact]
    public void SameOrigin_IsAllowed()
    {
        var decision = TerminalOriginPolicy.Evaluate(
            "https://panel.example.com",
            "panel.example.com",
            "https",
            allowMissingOrigin: true,
            NoOrigins);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void SameOriginWithPort_IsAllowed()
    {
        var decision = TerminalOriginPolicy.Evaluate(
            "http://127.0.0.1:5188",
            "127.0.0.1:5188",
            "http",
            allowMissingOrigin: true,
            NoOrigins);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void CrossOrigin_IsRejected()
    {
        var decision = TerminalOriginPolicy.Evaluate(
            "https://evil.example.com",
            "panel.example.com",
            "https",
            allowMissingOrigin: true,
            NoOrigins);

        Assert.False(decision.Allowed);
        Assert.Contains("Origin", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentPort_IsRejected()
    {
        var decision = TerminalOriginPolicy.Evaluate(
            "https://panel.example.com:8443",
            "panel.example.com:5188",
            "https",
            allowMissingOrigin: true,
            NoOrigins);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void AllowedOrigins_IsHonored()
    {
        var decision = TerminalOriginPolicy.Evaluate(
            "https://panel.example.com:8443",
            "internal-host:5188",
            "http",
            allowMissingOrigin: true,
            ["https://panel.example.com:8443"]);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void MissingOrigin_FollowsConfiguration()
    {
        Assert.True(TerminalOriginPolicy
            .Evaluate(null, "panel.example.com", "https", allowMissingOrigin: true, NoOrigins)
            .Allowed);

        Assert.False(TerminalOriginPolicy
            .Evaluate(null, "panel.example.com", "https", allowMissingOrigin: false, NoOrigins)
            .Allowed);
    }

    [Fact]
    public void NullOriginLiteral_IsRejected()
    {
        var decision = TerminalOriginPolicy.Evaluate(
            "null",
            "panel.example.com",
            "https",
            allowMissingOrigin: true,
            NoOrigins);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void InvalidOrigin_IsRejected()
    {
        var decision = TerminalOriginPolicy.Evaluate(
            "not-a-url",
            "panel.example.com",
            "https",
            allowMissingOrigin: true,
            NoOrigins);

        Assert.False(decision.Allowed);
    }
}
