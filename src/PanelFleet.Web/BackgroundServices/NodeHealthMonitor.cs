using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Web.Options;

namespace PanelFleet.Web.BackgroundServices;

/// <summary>周期巡检节点健康状态；每节点独立 scope，避免 DbContext 并发问题。</summary>
public sealed class NodeHealthMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HealthOptions _options;
    private readonly ILogger<NodeHealthMonitor> _logger;

    public NodeHealthMonitor(
        IServiceScopeFactory scopeFactory,
        HealthOptions options,
        ILogger<NodeHealthMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.IntervalSeconds, 15, 3600));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "健康巡检轮次失败。");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var listingScope = _scopeFactory.CreateScope();
        var listingService = listingScope.ServiceProvider.GetRequiredService<INodeService>();
        var ids = await listingService.ListEnabledIdsAsync(stoppingToken).ConfigureAwait(false);

        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(_options.MaxConcurrency, 1, 16),
                CancellationToken = stoppingToken
            },
            async (id, token) =>
            {
                await CheckNodeAsync(id, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task CheckNodeAsync(PanelFleet.Domain.Model.NodeId id, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<INodeService>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 3, 120)));

        try
        {
            await service.CheckNodeHealthAsync(id, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("节点 {NodeId} 巡检超时。", id);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "节点 {NodeId} 巡检失败。", id);
        }
    }
}
