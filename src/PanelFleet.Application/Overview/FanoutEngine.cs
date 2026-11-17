using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Overview;

/// <summary>目标调用返回 1Panel 业务错误时抛出的异常，携带结构化错误。</summary>
public sealed class PanelCallException : Exception
{
    public PanelCallException(PanelError error)
        : base(error.Message)
    {
        Error = error;
    }

    public PanelError Error { get; }
}

/// <summary>
/// 带并发上限、单节点超时、整轮预算的流式并发执行引擎。
/// 除调用方取消外，保证每个 target 恰好产出 1 条结果。
/// </summary>
public sealed class FanoutEngine : IFanoutEngine
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FanoutEngine> _logger;

    public FanoutEngine(TimeProvider timeProvider, ILogger<FanoutEngine> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async IAsyncEnumerable<FanoutResult<T>> RunAsync<T>(
        IReadOnlyList<FanoutTarget<T>> targets,
        FanoutOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        if (targets.Count == 0)
        {
            yield break;
        }

        var seen = new HashSet<NodeId>();
        foreach (var target in targets)
        {
            if (!seen.Add(target.NodeId))
            {
                throw new ArgumentException($"重复的 NodeId：{target.NodeId}", nameof(targets));
            }
        }

        var channel = Channel.CreateBounded<FanoutResult<T>>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(stopCts.Token);
        budgetCts.CancelAfter(options.Budget);

        var completed = new ConcurrentDictionary<NodeId, bool>();
        var producer = ProduceAsync(targets, options, channel.Writer, completed, stopCts, budgetCts);

        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return result;
            }
        }
        finally
        {
            stopCts.Cancel();
            budgetCts.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方停止时 producer 以取消结束，属预期。
            }
        }
    }

    private async Task ProduceAsync<T>(
        IReadOnlyList<FanoutTarget<T>> targets,
        FanoutOptions options,
        ChannelWriter<FanoutResult<T>> writer,
        ConcurrentDictionary<NodeId, bool> completed,
        CancellationTokenSource stopCts,
        CancellationTokenSource budgetCts)
    {
        try
        {
            await Parallel.ForEachAsync(
                targets,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaxConcurrency,
                    CancellationToken = budgetCts.Token
                },
                async (target, token) =>
                {
                    var result = await ExecuteTargetAsync(target, token, options, stopCts, budgetCts).ConfigureAwait(false);
                    await writer.WriteAsync(result, stopCts.Token).ConfigureAwait(false);
                    completed[target.NodeId] = true;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
        {
            // 调用方取消：不为未完成 target 补结果。
        }
        catch (OperationCanceledException)
        {
            // 整轮预算耗尽：进入 finally 补齐未完成 target。
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "聚合执行发生未预期异常。");
        }
        finally
        {
            if (!stopCts.IsCancellationRequested)
            {
                foreach (var target in targets)
                {
                    if (!completed.TryAdd(target.NodeId, true))
                    {
                        continue;
                    }

                    var error = new PanelError(
                        PanelErrorKind.Timeout,
                        $"[{target.NodeName}] 本轮总预算耗尽，请稍后重试。");
                    var result = new FanoutResult<T>(
                        target.NodeId,
                        FanoutStatus.BudgetExceeded,
                        default,
                        error,
                        TimeSpan.Zero,
                        _timeProvider.GetUtcNow());

                    try
                    {
                        await writer.WriteAsync(result, stopCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            writer.TryComplete();
        }
    }

    private async Task<FanoutResult<T>> ExecuteTargetAsync<T>(
        FanoutTarget<T> target,
        CancellationToken parallelToken,
        FanoutOptions options,
        CancellationTokenSource stopCts,
        CancellationTokenSource budgetCts)
    {
        using var targetCts = CancellationTokenSource.CreateLinkedTokenSource(parallelToken);
        targetCts.CancelAfter(options.PerTargetTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var data = await target.Call(targetCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new FanoutResult<T>(
                target.NodeId,
                FanoutStatus.Success,
                data,
                null,
                stopwatch.Elapsed,
                _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
        {
            throw;
        }
        catch (PanelCallException exception)
        {
            stopwatch.Stop();
            return new FanoutResult<T>(
                target.NodeId,
                FanoutStatus.Failed,
                default,
                exception.Error,
                stopwatch.Elapsed,
                _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var budgetExceeded = budgetCts.IsCancellationRequested;
            var error = budgetExceeded
                ? new PanelError(PanelErrorKind.Timeout, $"[{target.NodeName}] 本轮总预算耗尽，请稍后重试。")
                : new PanelError(
                    PanelErrorKind.Timeout,
                    $"[{target.NodeName}] 节点响应超时（{options.PerTargetTimeout.TotalSeconds:0.#} 秒）。");

            return new FanoutResult<T>(
                target.NodeId,
                budgetExceeded ? FanoutStatus.BudgetExceeded : FanoutStatus.TimedOut,
                default,
                error,
                stopwatch.Elapsed,
                _timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stopwatch.Stop();
            return new FanoutResult<T>(
                target.NodeId,
                FanoutStatus.Failed,
                default,
                new PanelError(
                    PanelErrorKind.Protocol,
                    $"[{target.NodeName}] 采集失败：{exception.Message}",
                    Cause: exception),
                stopwatch.Elapsed,
                _timeProvider.GetUtcNow());
        }
    }

    private static void ValidateOptions(FanoutOptions options)
    {
        if (options.MaxConcurrency is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency 必须在 1..64 之间。");
        }

        if (options.PerTargetTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PerTargetTimeout 必须大于 0。");
        }

        if (options.Budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Budget 必须大于 0。");
        }

        if (options.ChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ChannelCapacity 必须大于 0。");
        }
    }
}
