using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Overview;

namespace PanelFleet.Web.BackgroundServices;

/// <summary>
/// 总览后台刷新宿主：启动后延迟 3s 请求首轮；之后按间隔请求整轮刷新；
/// 串行消费队列（同一时刻最多 1 个整轮刷新）。间隔为 0/负数时不自动刷新。
/// </summary>
public sealed class OverviewRefreshHost : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOverviewRefreshQueue _queue;
    private readonly OverviewOptions _options;
    private readonly ILogger<OverviewRefreshHost> _logger;

    public OverviewRefreshHost(
        IServiceScopeFactory scopeFactory,
        IOverviewRefreshQueue queue,
        OverviewOptions options,
        ILogger<OverviewRefreshHost> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var autoRefresh = _options.RefreshIntervalSeconds > 0;
        if (autoRefresh)
        {
            _queue.RequestFull();
        }

        var ticker = autoRefresh ? RunTickerAsync(stoppingToken) : null;

        try
        {
            await foreach (var _ in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 应用停止。
        }
        finally
        {
            if (ticker is not null)
            {
                try
                {
                    await ticker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 预期停止。
                }
            }
        }
    }

    private async Task RunTickerAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.RefreshIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                _queue.RequestFull();
            }
        }
        catch (OperationCanceledException)
        {
            // 应用停止。
        }
    }

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IOverviewService>();
            await service.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 应用停止。
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "总览后台刷新失败。");
        }
    }
}
