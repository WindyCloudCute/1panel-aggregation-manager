using System.Net;
using System.Security.Authentication;
using PanelFleet.Client.Errors;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests;

public sealed class PanelErrorMapperTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PanelErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, PanelErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, PanelErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, PanelErrorKind.Validation)]
    [InlineData(HttpStatusCode.InternalServerError, PanelErrorKind.PanelServerError)]
    [InlineData(HttpStatusCode.Redirect, PanelErrorKind.Protocol)]
    public void FromHttpStatus_MapsExpectedKind(HttpStatusCode status, PanelErrorKind expected)
    {
        var error = PanelErrorMapper.FromHttpStatus(status, "msg", null, "node-1");

        Assert.Equal(expected, error.Kind);
        Assert.True(error.Message.Contains("[node-1]", StringComparison.Ordinal));
        Assert.True(error.Message.Contains("1Panel: msg", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(401, PanelErrorKind.Unauthorized)]
    [InlineData(403, PanelErrorKind.Forbidden)]
    [InlineData(404, PanelErrorKind.NotFound)]
    [InlineData(400, PanelErrorKind.Validation)]
    [InlineData(500, PanelErrorKind.PanelServerError)]
    [InlineData(418, PanelErrorKind.Protocol)]
    public void FromPanelCode_MapsExpectedKind(int code, PanelErrorKind expected)
    {
        var error = PanelErrorMapper.FromPanelCode(code, "boom", "E1", "node-1");

        Assert.Equal(expected, error.Kind);
        Assert.Equal(code.ToString(System.Globalization.CultureInfo.InvariantCulture), error.PanelCode);
        Assert.Equal("E1", error.ErrorCode);
    }

    [Fact]
    public void Transport_AuthenticationException_MapsToTls()
    {
        var exception = new HttpRequestException("tls", new AuthenticationException("cert"));

        var error = PanelErrorMapper.MapTransport(exception, "node-1");

        Assert.Equal(PanelErrorKind.Tls, error.Kind);
    }

    [Fact]
    public void Transport_OtherException_MapsToNetwork()
    {
        var exception = new HttpRequestException("refused");

        var error = PanelErrorMapper.MapTransport(exception, "node-1");

        Assert.Equal(PanelErrorKind.Network, error.Kind);
    }
}
