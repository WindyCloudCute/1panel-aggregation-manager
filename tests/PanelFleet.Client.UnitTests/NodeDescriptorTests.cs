using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests;

public sealed class NodeDescriptorTests
{
    [Fact]
    public void ToString_DoesNotLeakApiKey()
    {
        var node = new NodeDescriptor(
            NodeId.New(),
            "node-1",
            new Uri("https://panel.example.test"),
            "super-secret-api-key");

        var text = node.ToString();

        Assert.DoesNotContain("super-secret-api-key", text, StringComparison.Ordinal);
        Assert.Contains("node-1", text, StringComparison.Ordinal);
    }
}
