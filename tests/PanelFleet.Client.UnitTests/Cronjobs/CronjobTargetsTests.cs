using System.Text.Json.Nodes;
using PanelFleet.Application.Cronjobs;
using PanelFleet.Application.Operations;

namespace PanelFleet.Client.UnitTests.Cronjobs;

public sealed class CronjobTargetsTests
{
    private static OperationParameters Parameters(JsonObject values) => OperationParameters.From(values);

    [Fact]
    public void Parse_SingleCronjob_AppliesToAllNodes()
    {
        var first = CronjobTestData.Node("node-a");
        var second = CronjobTestData.Node("node-b");

        var map = CronjobTargets.Parse(
            Parameters(new JsonObject { ["cronjob"] = 5 }),
            [first, second],
            maxBatch: 20);

        Assert.Equal(2, map.Count);
        Assert.Equal([5L], map[first.Id.Value]);
        Assert.Equal([5L], map[second.Id.Value]);
    }

    [Fact]
    public void Parse_IdsArray_AppliesToAllNodes()
    {
        var node = CronjobTestData.Node("node-a");

        var map = CronjobTargets.Parse(
            Parameters(new JsonObject { ["ids"] = new JsonArray(1, 2, 3) }),
            [node],
            maxBatch: 20);

        Assert.Equal([1L, 2L, 3L], map[node.Id.Value]);
    }

    [Fact]
    public void Parse_ByNode_RequiresEverySelectedNode()
    {
        var first = CronjobTestData.Node("node-a");
        var second = CronjobTestData.Node("node-b");
        var parameters = new JsonObject
        {
            ["cronjobsByNode"] = new JsonObject
            {
                [first.Id.Value.ToString("D")] = new JsonArray(1)
            }
        };

        var exception = Assert.Throws<OperationValidationException>(
            () => CronjobTargets.Parse(Parameters(parameters), [first, second], maxBatch: 20));

        Assert.Contains("node-b", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ByNode_MapsIdsPerNode()
    {
        var first = CronjobTestData.Node("node-a");
        var second = CronjobTestData.Node("node-b");
        var parameters = new JsonObject
        {
            ["cronjobsByNode"] = new JsonObject
            {
                [first.Id.Value.ToString("D")] = new JsonArray(1, 2),
                [second.Id.Value.ToString("D")] = new JsonArray(7)
            }
        };

        var map = CronjobTargets.Parse(Parameters(parameters), [first, second], maxBatch: 20);

        Assert.Equal([1L, 2L], map[first.Id.Value]);
        Assert.Equal([7L], map[second.Id.Value]);
    }

    [Fact]
    public void Parse_UnknownNode_ThrowsUnlessIgnored()
    {
        var node = CronjobTestData.Node("node-a");
        var parameters = new JsonObject
        {
            ["cronjobsByNode"] = new JsonObject
            {
                [node.Id.Value.ToString("D")] = new JsonArray(1),
                [Guid.NewGuid().ToString("D")] = new JsonArray(2)
            }
        };

        Assert.Throws<OperationValidationException>(
            () => CronjobTargets.Parse(Parameters(parameters), [node], maxBatch: 20));

        var map = CronjobTargets.Parse(Parameters(parameters), [node], maxBatch: 20, ignoreUnknownNodes: true);
        Assert.Single(map);
    }

    [Fact]
    public void Parse_EnforcesBatchLimit()
    {
        var node = CronjobTestData.Node("node-a");

        Assert.Throws<OperationValidationException>(
            () => CronjobTargets.Parse(
                Parameters(new JsonObject { ["ids"] = new JsonArray(1, 2, 3) }),
                [node],
                maxBatch: 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Parse_RejectsInvalidIds(long id)
    {
        var node = CronjobTestData.Node("node-a");

        Assert.Throws<OperationValidationException>(
            () => CronjobTargets.Parse(
                Parameters(new JsonObject { ["ids"] = new JsonArray(id) }),
                [node],
                maxBatch: 20));
    }

    [Fact]
    public void Parse_MissingTargets_Throws()
    {
        var node = CronjobTestData.Node("node-a");

        Assert.Throws<OperationValidationException>(
            () => CronjobTargets.Parse(Parameters(new JsonObject()), [node], maxBatch: 20));
    }
}
