using PanelFleet.Application.Models;
using PanelFleet.Application.Overview;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Overview;

public sealed class OverviewCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 6, 0, 0, TimeSpan.Zero);

    private static NodeOverviewData Data(DateTimeOffset attemptedAt, bool success = true) => new(
        Metrics: null,
        Error: success ? null : new PanelError(PanelErrorKind.Network, "boom"),
        FetchedAt: success ? attemptedAt : null,
        AttemptedAt: attemptedAt,
        LastLatency: TimeSpan.FromMilliseconds(12),
        LastAttemptSucceeded: success);

    [Fact]
    public void ApplyNode_FirstWriteIsAccepted()
    {
        var cache = new OverviewCache();
        var id = NodeId.New();

        Assert.True(cache.ApplyNode(id, Data(T0)));
        Assert.Single(cache.GetSnapshot().Nodes);
    }

    [Fact]
    public void ApplyNode_RejectsOlderAttempt_AndAcceptsNewer()
    {
        var cache = new OverviewCache();
        var id = NodeId.New();

        Assert.True(cache.ApplyNode(id, Data(T0.AddSeconds(10))));
        Assert.False(cache.ApplyNode(id, Data(T0)));

        Assert.True(cache.ApplyNode(id, Data(T0.AddSeconds(20))));
        Assert.Equal(T0.AddSeconds(20), cache.GetSnapshot().Nodes[id].AttemptedAt);
    }

    [Fact]
    public void Prune_RemovesUnknownNodes()
    {
        var cache = new OverviewCache();
        var keep = NodeId.New();
        var remove = NodeId.New();

        cache.ApplyNode(keep, Data(T0));
        cache.ApplyNode(remove, Data(T0));
        cache.Prune(new HashSet<NodeId> { keep });

        var snapshot = cache.GetSnapshot();
        Assert.True(snapshot.Nodes.ContainsKey(keep));
        Assert.False(snapshot.Nodes.ContainsKey(remove));
    }

    [Fact]
    public void RefreshingAndGeneration_AreTracked()
    {
        var cache = new OverviewCache();

        cache.SetRefreshing(true);
        Assert.True(cache.GetSnapshot().IsRefreshing);

        cache.SetRefreshing(false);
        var before = cache.GetSnapshot().Generation;
        cache.MarkRunCompleted(TimeSpan.FromMilliseconds(321));

        var snapshot = cache.GetSnapshot();
        Assert.False(snapshot.IsRefreshing);
        Assert.Equal(TimeSpan.FromMilliseconds(321), snapshot.LastRunElapsed);
        Assert.NotNull(snapshot.LastCompletedAt);
        Assert.True(snapshot.Generation > before);
    }
}
