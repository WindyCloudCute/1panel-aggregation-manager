using System.Collections.Concurrent;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Operations;

/// <summary>写操作守卫：节点写锁 + 相同操作指纹去重。</summary>
public sealed class OperationGuards
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    /// <summary>尝试获取节点写锁；超时返回 null（节点正在执行其他写操作）。</summary>
    public async Task<NodeOperationLease?> TryAcquireNodeAsync(
        NodeId nodeId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(nodeId.Value, static _ => new SemaphoreSlim(1, 1));
        var acquired = await semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return acquired ? new NodeOperationLease(semaphore) : null;
    }

    /// <summary>判断相同操作指纹是否在去重窗口内已提交。</summary>
    public bool IsDuplicate(string fingerprint, DateTimeOffset now, TimeSpan window)
    {
        Prune(now, window);
        return _recent.TryGetValue(fingerprint, out var executedAt) && now - executedAt < window;
    }

    public void MarkExecuted(string fingerprint, DateTimeOffset now) => _recent[fingerprint] = now;

    private void Prune(DateTimeOffset now, TimeSpan window)
    {
        if (_recent.Count < 512)
        {
            return;
        }

        foreach (var pair in _recent)
        {
            if (now - pair.Value >= window)
            {
                _recent.TryRemove(pair.Key, out _);
            }
        }
    }
}

/// <summary>节点写锁租约，释放时归还信号量。</summary>
public sealed class NodeOperationLease : IDisposable
{
    private SemaphoreSlim? _semaphore;

    internal NodeOperationLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

    public void Dispose()
    {
        var semaphore = Interlocked.Exchange(ref _semaphore, null);
        if (semaphore is null)
        {
            return;
        }

        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // 节点锁对象被回收（进程退出路径），忽略。
        }
    }
}
