namespace PanelFleet.Client.UnitTests;

public sealed class PanelEndpointTests
{
    [Theory]
    [InlineData("https://host", "api/v2/dashboard/base/os", "https://host/api/v2/dashboard/base/os")]
    [InlineData("https://host/", "api/v2/dashboard/base/os", "https://host/api/v2/dashboard/base/os")]
    [InlineData("https://host/panel/", "/api/v2/dashboard/base/os", "https://host/panel/api/v2/dashboard/base/os")]
    [InlineData("https://host:8443/panel", "api/v2/x?io=all", "https://host:8443/panel/api/v2/x?io=all")]
    public void Combine_BuildsExpectedUrl(string baseUri, string relativePath, string expected)
    {
        var actual = PanelEndpoint.Combine(new Uri(baseUri), relativePath);

        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void Combine_RejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => PanelEndpoint.Combine(new Uri("https://host"), " "));
    }
}
