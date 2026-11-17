using PanelFleet.Application.Nodes;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests;

public sealed class NodeHealthEvaluatorTests
{
    [Fact]
    public void NoError_IsOnline()
    {
        var (status, message) = NodeHealthEvaluator.Evaluate(null);

        Assert.Equal(NodeHealth.Online, status);
        Assert.Null(message);
    }

    [Theory]
    [InlineData(PanelErrorKind.Unauthorized, NodeHealth.Offline)]
    [InlineData(PanelErrorKind.Forbidden, NodeHealth.Offline)]
    [InlineData(PanelErrorKind.Network, NodeHealth.Offline)]
    [InlineData(PanelErrorKind.Tls, NodeHealth.Offline)]
    [InlineData(PanelErrorKind.Timeout, NodeHealth.Offline)]
    [InlineData(PanelErrorKind.NotFound, NodeHealth.Degraded)]
    [InlineData(PanelErrorKind.Unsupported, NodeHealth.Degraded)]
    [InlineData(PanelErrorKind.Protocol, NodeHealth.Degraded)]
    [InlineData(PanelErrorKind.PanelServerError, NodeHealth.Degraded)]
    public void ErrorKind_MapsToExpectedStatus(PanelErrorKind kind, NodeHealth expected)
    {
        var (status, message) = NodeHealthEvaluator.Evaluate(new PanelError(kind, "boom"));

        Assert.Equal(expected, status);
        Assert.NotNull(message);
    }
}
