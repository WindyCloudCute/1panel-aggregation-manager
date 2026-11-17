using PanelFleet.Application.PanelUpdates;

namespace PanelFleet.Client.UnitTests.PanelUpdates;

public sealed class PanelVersionComparerTests
{
    [Theory]
    [InlineData("v2.3.2", "v2.5.1", -1)]
    [InlineData("v2.5.1", "v2.3.2", 1)]
    [InlineData("v2.3.2", "v2.3.2", 0)]
    [InlineData("2.3.2", "v2.3.2", 0)]
    [InlineData("v2.10.0", "v2.9.9", 1)]
    [InlineData("v2.3", "v2.3.0", 0)]
    [InlineData("v2.3.2-lts", "v2.3.2", 0)]
    [InlineData("v2.5.1-beta.1", "v2.5.1", -1)]
    [InlineData("v2.5.1", "v2.5.1-beta.1", 1)]
    [InlineData("v2.5.1-beta.2", "v2.5.1-beta.1", 1)]
    [InlineData("v3.0.0", "v2.99.99", 1)]
    public void Compare_OrdersVersions(string left, string right, int expectedSign)
    {
        var actual = PanelVersionComparer.Compare(left, right);

        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Theory]
    [InlineData("v2.5.1", "v2.3.2", true)]
    [InlineData("v2.3.2", "v2.5.1", false)]
    [InlineData("v2.3.2", "v2.3.2", false)]
    [InlineData("v2.5.1", null, false)]
    [InlineData("v2.5.1", "not-a-version", false)]
    public void IsUpgrade_OnlyTrueWhenTargetIsHigher(string? target, string? current, bool expected)
    {
        Assert.Equal(expected, PanelVersionComparer.IsUpgrade(target, current));
    }

    [Theory]
    [InlineData("v2.3.2", 2, 3, 2, null)]
    [InlineData("2.3.2", 2, 3, 2, null)]
    [InlineData("v2.5.1-beta.1", 2, 5, 1, "beta.1")]
    [InlineData("v2.3.2-lts", 2, 3, 2, null)]
    [InlineData("v2.3.2.4", 2, 3, 2, null)]
    public void TryParse_ParsesPanelVersionFormats(
        string value,
        int major,
        int minor,
        int patch,
        string? preRelease)
    {
        Assert.True(PanelVersionComparer.TryParse(value, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("v")]
    [InlineData("v2.x.1")]
    [InlineData("v2.3.2.4.5")]
    public void TryParse_RejectsInvalidInput(string? value)
    {
        Assert.False(PanelVersionComparer.TryParse(value, out _));
    }

    [Fact]
    public void Compare_NullVersions_AreTreatedAsLower()
    {
        Assert.Equal(0, PanelVersionComparer.Compare(null, null));
        Assert.True(PanelVersionComparer.Compare(null, "v2.0.0") < 0);
        Assert.True(PanelVersionComparer.Compare("v2.0.0", null) > 0);
    }
}
