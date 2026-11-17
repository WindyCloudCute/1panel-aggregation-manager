using PanelFleet.Application.Containers;

namespace PanelFleet.Client.UnitTests.Containers;

public sealed class SecretMaskerTests
{
    [Theory]
    [InlineData("MYSQL_ROOT_PASSWORD", true)]
    [InlineData("DB_PWD", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("AWS_SECRET_ACCESS_KEY", true)]
    [InlineData("API_KEY", true)]
    [InlineData("TZ", false)]
    [InlineData("HOME", false)]
    [InlineData("PATH", false)]
    [InlineData("", false)]
    public void IsSensitiveKey_MatchesCredentialNames(string key, bool expected) =>
        Assert.Equal(expected, SecretMasker.IsSensitiveKey(key));

    [Theory]
    [InlineData("MYSQL_ROOT_PASSWORD=super-secret", "MYSQL_ROOT_PASSWORD=***")]
    [InlineData("TZ=Asia/Shanghai", "TZ=Asia/Shanghai")]
    [InlineData("NO_EQUALS", "NO_EQUALS")]
    [InlineData("", "")]
    public void MaskEntry_MasksOnlySensitiveValues(string entry, string expected) =>
        Assert.Equal(expected, SecretMasker.MaskEntry(entry));

    [Fact]
    public void MaskValue_MasksSensitiveValue()
    {
        Assert.Equal(SecretMasker.Mask, SecretMasker.MaskValue("DB_PASSWORD", "abc"));
        Assert.Equal("/usr/bin", SecretMasker.MaskValue("PATH", "/usr/bin"));
        Assert.Equal(string.Empty, SecretMasker.MaskValue("TZ", null));
    }
}
