using PanelFleet.Application.Models;

namespace PanelFleet.Application.Abstractions;

/// <summary>总览事件广播器（单例）；每个 Blazor 电路订阅一份。</summary>
public interface IOverviewBroadcaster
{
    int SubscriberCount { get; }

    void Publish(OverviewEvent overviewEvent);

    IOverviewSubscription Subscribe();
}

/// <summary>订阅句柄：释放即取消订阅。</summary>
public interface IOverviewSubscription : IAsyncDisposable
{
    IAsyncEnumerable<OverviewEvent> Events { get; }
}
