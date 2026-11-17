using System.Collections.Concurrent;

namespace PanelFleet.Application.Operations;

/// <summary>已预览、等待确认的写操作（含一次性 Token 与操作者绑定）。</summary>
public sealed record OperationConfirmation(
    Guid TokenId,
    OperationInvocation Invocation,
    OperationPlan Plan,
    string ActorKey,
    OperationPreview Preview,
    DateTimeOffset ExpiresAt);

public interface IConfirmationStore
{
    OperationConfirmation Create(
        OperationInvocation invocation,
        OperationPlan plan,
        string actorKey,
        OperationPreview preview,
        DateTimeOffset expiresAt);

    /// <summary>取出并删除确认（一次性）；不存在、过期或操作者不匹配时返回 null。</summary>
    OperationConfirmation? TryConsume(Guid tokenId, string actorKey, DateTimeOffset now);

    OperationConfirmation? Peek(Guid tokenId);

    int PendingCount { get; }
}

/// <summary>内存确认存储：单次有效、TTL 过期、绑定操作者。进程重启后全部失效。</summary>
public sealed class ConfirmationStore : IConfirmationStore
{
    private readonly ConcurrentDictionary<Guid, OperationConfirmation> _items = new();
    private readonly TimeProvider _timeProvider;
    private int _sweepCounter;

    public ConfirmationStore(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public int PendingCount => _items.Count;

    public OperationConfirmation Create(
        OperationInvocation invocation,
        OperationPlan plan,
        string actorKey,
        OperationPreview preview,
        DateTimeOffset expiresAt)
    {
        var confirmation = new OperationConfirmation(
            preview.TokenId,
            invocation,
            plan,
            actorKey,
            preview,
            expiresAt);

        _items[confirmation.TokenId] = confirmation;
        SweepExpired();
        return confirmation;
    }

    public OperationConfirmation? TryConsume(Guid tokenId, string actorKey, DateTimeOffset now)
    {
        if (!_items.TryRemove(tokenId, out var confirmation))
        {
            return null;
        }

        if (confirmation.ExpiresAt <= now)
        {
            return null;
        }

        return string.Equals(confirmation.ActorKey, actorKey, StringComparison.Ordinal) ? confirmation : null;
    }

    public OperationConfirmation? Peek(Guid tokenId) =>
        _items.TryGetValue(tokenId, out var confirmation) && confirmation.ExpiresAt > _timeProvider.GetUtcNow()
            ? confirmation
            : null;

    private void SweepExpired()
    {
        // 每 32 次创建清理一次，避免每次都全量遍历。
        if (Interlocked.Increment(ref _sweepCounter) % 32 != 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _items)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _items.TryRemove(pair.Key, out _);
            }
        }
    }
}
