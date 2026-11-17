namespace PanelFleet.Application.Abstractions;

/// <summary>一次整轮刷新请求。</summary>
public sealed record OverviewRefreshRequest(DateTimeOffset RequestedAt);

/// <summary>整轮刷新请求队列（单例，合并重复请求）。</summary>
public interface IOverviewRefreshQueue
{
    /// <summary>请求一轮刷新；已有待处理请求时合并（不重复入队）。</summary>
    void RequestFull();

    int PendingCount { get; }

    IAsyncEnumerable<OverviewRefreshRequest> ReadAllAsync(CancellationToken cancellationToken);
}
