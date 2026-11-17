using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Overview;

namespace PanelFleet.Client.UnitTests.Overview;

public sealed class OverviewBroadcasterTests
{
    [Fact]
    public async Task MultipleSubscribers_ReceiveEvents_AndDisposeUnsubscribes()
    {
        var broadcaster = new OverviewBroadcaster();
        var first = broadcaster.Subscribe();
        var second = broadcaster.Subscribe();

        Assert.Equal(2, broadcaster.SubscriberCount);

        var snapshot = OverviewSnapshotFactory.Build([], DateTimeOffset.UtcNow, false, null, 0);
        broadcaster.Publish(new OverviewEvent.RunCompleted(snapshot, TimeSpan.Zero, false));

        Assert.True(await ReadAsync(first));
        Assert.True(await ReadAsync(second));

        await first.DisposeAsync();
        Assert.Equal(1, broadcaster.SubscriberCount);

        broadcaster.Publish(new OverviewEvent.RunCompleted(snapshot, TimeSpan.Zero, false));
        Assert.True(await ReadAsync(second));
    }

    private static async Task<bool> ReadAsync(IOverviewSubscription subscription, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            await using var enumerator = subscription.Events.GetAsyncEnumerator(cts.Token);
            return await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
