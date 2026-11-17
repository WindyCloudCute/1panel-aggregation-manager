using System.Collections.Concurrent;

namespace PanelFleet.Application.Operations;

public interface IOperationHistoryStore
{
    void Add(OperationExecution execution);

    OperationExecution? TryGet(Guid operationId);

    IReadOnlyList<OperationExecution> ListRecent(int take);
}

/// <summary>内存执行历史（环形缓冲）。完整记录以审计表为准。</summary>
public sealed class OperationHistoryStore : IOperationHistoryStore
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OperationExecution> _items = new();
    private readonly Queue<Guid> _order = new();

    public OperationHistoryStore(OperationOptions options) => _capacity = options.HistoryCapacity;

    public void Add(OperationExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        lock (_gate)
        {
            if (_items.ContainsKey(execution.Id))
            {
                return;
            }

            _items[execution.Id] = execution;
            _order.Enqueue(execution.Id);

            while (_order.Count > _capacity)
            {
                var evicted = _order.Dequeue();
                _items.Remove(evicted);
            }
        }
    }

    public OperationExecution? TryGet(Guid operationId)
    {
        lock (_gate)
        {
            return _items.TryGetValue(operationId, out var execution) ? execution : null;
        }
    }

    public IReadOnlyList<OperationExecution> ListRecent(int take)
    {
        var limit = Math.Clamp(take, 1, _capacity);
        lock (_gate)
        {
            return _items.Values
                .OrderByDescending(execution => execution.StartedAt)
                .Take(limit)
                .ToArray();
        }
    }
}
