using System.Collections.Concurrent;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Overview;

/// <summary>
/// 按节点的采集闸门：保证同一节点同时只有 1 个采集（整轮刷新与单节点重试并行时）。
/// 单例注册；节点数量少（≤数十），entry 不清理。
/// </summary>
public sealed class OverviewRefreshGates
{
    private readonly ConcurrentDictionary<NodeId, SemaphoreSlim> _gates = new();

    public async ValueTask<IDisposable> AcquireAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(nodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
