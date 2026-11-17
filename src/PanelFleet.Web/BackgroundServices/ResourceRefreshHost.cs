using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Resources;

namespace PanelFleet.Web.BackgroundServices;

/// <summary>
/// P4 后台定时刷新：只刷新最近打开过的资源类型，按类型串行执行。
/// 间隔为 0 时只保留手动刷新。
/// </summary>
public sealed class ResourceRefreshHost : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IResourceAccessTracker _tracker;
    private readonly ResourceOptions _options;
    private readonly ILogger<ResourceRefreshHost> _logger;

    public ResourceRefreshHost(
        IServiceScopeFactory scopeFactory,
        IResourceAccessTracker tracker,
        ResourceOptions options,
        ILogger<ResourceRefreshHost> logger)
    {
        _scopeFactory = scopeFactory;
        _tracker = tracker;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RefreshIntervalSeconds <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.RefreshIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var kinds = _tracker.RecentlyAccessed(DateTimeOffset.UtcNow);
                foreach (var kind in kinds)
                {
                    await RefreshKindAsync(kind, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 应用停止。
        }
    }

    private async Task RefreshKindAsync(ResourceKind kind, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IResourceQueryService>();
            await service
                .RefreshAsync(kind, new ResourceQuery(null, null, false, 1, 50, null), stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 应用停止。
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "后台刷新资源类型 {Kind} 失败。", kind);
        }
    }
}
