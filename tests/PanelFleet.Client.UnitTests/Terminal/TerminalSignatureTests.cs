using PanelFleet.Application.Terminal;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalSignatureTests
{
    [Fact]
    public void Compute_HmacSha256_MatchesKnownVector()
    {
        var token = TerminalSignature.Compute("unit-test-key", 1700000000, AuthMode.HmacSha256);

        Assert.Equal("254d967771b5c9bfb5ea37050fd94c73bc80e8a60e7d7aa06111d607235767ed", token);
    }

    [Fact]
    public void Compute_Md5_MatchesKnownVector()
    {
        var token = TerminalSignature.Compute("unit-test-key", 1700000000, AuthMode.Md5);

        Assert.Equal("58c8a9db972a1805892bd9a01059840b", token);
    }

    [Fact]
    public void Compute_IsLowercaseHexAndTimestampSensitive()
    {
        var first = TerminalSignature.Compute("unit-test-key", 1700000001, AuthMode.HmacSha256);
        var second = TerminalSignature.Compute("unit-test-key", 1700000002, AuthMode.HmacSha256);

        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Matches("^[0-9a-f]{32}$", TerminalSignature.Compute("unit-test-key", 1700000001, AuthMode.Md5));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_AutoMode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TerminalSignature.Compute("unit-test-key", 1700000000, AuthMode.Auto));
    }

    [Fact]
    public void Compute_EmptyApiKey_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => TerminalSignature.Compute(string.Empty, 1700000000, AuthMode.Md5));
    }
}
