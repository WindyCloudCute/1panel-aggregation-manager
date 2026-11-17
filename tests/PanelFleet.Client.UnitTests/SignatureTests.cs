using PanelFleet.Client.Signing;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests;

public sealed class SignatureTests
{
    private const string ApiKey = "test-api-key";
    private const long Timestamp = 1700000000L;

    [Fact]
    public void HmacSha256_MatchesKnownVector()
    {
        var token = ApiKeySigner.ComputeToken(ApiKey, Timestamp, AuthMode.HmacSha256);

        Assert.Equal("751a37be9a78d4a8e8de84a9d455b1d351f571994b02228c67580b42b5c78fc5", token);
    }

    [Fact]
    public void Md5_MatchesKnownVector()
    {
        var token = ApiKeySigner.ComputeToken(ApiKey, Timestamp, AuthMode.Md5);

        Assert.Equal("56115940aa5a7a82ff1f716c2cddb4d5", token);
    }

    [Fact]
    public void AutoMode_IsRejectedBySigner()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ApiKeySigner.ComputeToken(ApiKey, Timestamp, AuthMode.Auto));
    }

    [Fact]
    public void EmptyApiKey_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => ApiKeySigner.ComputeToken(string.Empty, Timestamp, AuthMode.HmacSha256));
    }
}
