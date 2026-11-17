using System.Threading.Channels;
using PanelFleet.Application.Abstractions;

namespace PanelFleet.Application.Overview;

/// <summary>整轮刷新请求队列：合并重复请求（同一时刻最多 1 个待处理请求）。</summary>
public sealed class OverviewRefreshQueue : IOverviewRefreshQueue
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private int _pending;

    public int PendingCount => Volatile.Read(ref _pending);

    public void RequestFull()
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
        {
            return;
        }

        _channel.Writer.TryWrite(0);
    }

    public async IAsyncEnumerable<OverviewRefreshRequest> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var _ in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _pending, 0);
            yield return new OverviewRefreshRequest(DateTimeOffset.UtcNow);
        }
    }
}
