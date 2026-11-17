using PanelFleet.Application.Models;
using PanelFleet.Application.Nodes;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests;

public sealed class NodeValidatorTests
{
    [Fact]
    public void ValidCreate_HasNoErrors()
    {
        var errors = NodeValidator.Validate(new CreateNodeRequest(
            "node-1",
            "https://panel.example.com",
            "test-api-key-1234"));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://host")]
    [InlineData("https://host/path?query=1")]
    [InlineData("not-a-url")]
    public void InvalidBaseUrl_IsReported(string? baseUrl)
    {
        var errors = NodeValidator.Validate(new CreateNodeRequest("node-1", baseUrl, "test-api-key-1234"));

        Assert.True(errors.ContainsKey("baseUrl"));
    }

    [Fact]
    public void MissingApiKey_IsReported()
    {
        var errors = NodeValidator.Validate(new CreateNodeRequest("node-1", "https://host", null));

        Assert.True(errors.ContainsKey("apiKey"));
    }

    [Fact]
    public void TooLongName_IsReported()
    {
        var errors = NodeValidator.Validate(new CreateNodeRequest(
            new string('a', NodeValidator.MaxNameLength + 1),
            "https://host",
            "test-api-key-1234"));

        Assert.True(errors.ContainsKey("name"));
    }

    [Fact]
    public void TooManyTags_IsReported()
    {
        var tags = Enumerable.Range(0, NodeValidator.MaxTags + 1).Select(index => $"tag-{index}").ToArray();
        var errors = NodeValidator.Validate(new CreateNodeRequest(
            "node-1",
            "https://host",
            "test-api-key-1234",
            Tags: tags));

        Assert.True(errors.ContainsKey("tags"));
    }

    [Theory]
    [InlineData("https://host/", "https://host/")]
    [InlineData("https://host/panel/", "https://host/panel")]
    [InlineData("https://host:8443/panel", "https://host:8443/panel")]
    public void NormalizeBaseUrl_RemovesTrailingSlash(string input, string expected)
    {
        var normalized = NodeValidator.NormalizeBaseUrl(input, out var error);

        Assert.Null(error);
        Assert.NotNull(normalized);
        Assert.Equal(expected, normalized.ToString());
    }

    [Theory]
    [InlineData("myentrance", "myentrance")]
    [InlineData("/myentrance/", "myentrance")]
    [InlineData("", null)]
    public void NormalizeSecurityEntrance_ReturnsExpected(string input, string? expected)
    {
        var normalized = NodeValidator.NormalizeSecurityEntrance(input, out var error);

        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a?x")]
    [InlineData("a#x")]
    public void InvalidSecurityEntrance_IsReported(string entrance)
    {
        var errors = NodeValidator.Validate(new CreateNodeRequest(
            "node-1",
            "https://host",
            "test-api-key-1234",
            SecurityEntrance: entrance));

        Assert.True(errors.ContainsKey("securityEntrance"));
    }

    [Fact]
    public void UpdateWithoutApiKey_IsAllowed()
    {
        var errors = NodeValidator.Validate(new UpdateNodeRequest(
            "node-1",
            "https://host",
            null,
            AuthMode.Auto,
            TlsPolicy.Strict));

        Assert.Empty(errors);
    }
}
