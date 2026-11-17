using PanelFleet.Application.Overview;

namespace PanelFleet.Client.UnitTests.Overview;

public sealed class OverviewRefreshQueueTests
{
    [Fact]
    public async Task DuplicateRequests_AreCoalesced()
    {
        var queue = new OverviewRefreshQueue();

        queue.RequestFull();
        queue.RequestFull();
        queue.RequestFull();

        Assert.Equal(1, queue.PendingCount);

        await using var enumerator = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(0, queue.PendingCount);

        queue.RequestFull();
        Assert.Equal(1, queue.PendingCount);
        Assert.True(await enumerator.MoveNextAsync());
    }
}
