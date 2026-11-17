using System.Collections.Concurrent;
using System.Threading.Channels;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;

namespace PanelFleet.Application.Overview;

/// <summary>总览事件广播器；每个订阅者一个独立的有界 channel（满则丢最旧）。</summary>
public sealed class OverviewBroadcaster : IOverviewBroadcaster
{
    private const int SubscriberCapacity = 256;

    private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public void Publish(OverviewEvent overviewEvent)
    {
        ArgumentNullException.ThrowIfNull(overviewEvent);

        foreach (var subscription in _subscribers.Values)
        {
            subscription.TryPublish(overviewEvent);
        }
    }

    public IOverviewSubscription Subscribe()
    {
        var subscription = new Subscription(this);
        _subscribers[subscription.Id] = subscription;
        return subscription;
    }

    private void Remove(Guid id) => _subscribers.TryRemove(id, out _);

    private sealed class Subscription : IOverviewSubscription
    {
        private readonly OverviewBroadcaster _owner;
        private readonly Channel<OverviewEvent> _channel = Channel.CreateBounded<OverviewEvent>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public Subscription(OverviewBroadcaster owner)
        {
            _owner = owner;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public IAsyncEnumerable<OverviewEvent> Events => _channel.Reader.ReadAllAsync();

        public void TryPublish(OverviewEvent overviewEvent) => _channel.Writer.TryWrite(overviewEvent);

        public ValueTask DisposeAsync()
        {
            _owner.Remove(Id);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
