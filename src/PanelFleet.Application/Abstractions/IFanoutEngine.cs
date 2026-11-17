using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

/// <summary>一个待并发执行的节点调用。</summary>
public sealed record FanoutTarget<T>(NodeId NodeId, string NodeName, Func<CancellationToken, Task<T>> Call);

/// <summary>单个节点的执行结果状态。</summary>
public enum FanoutStatus
{
    Success = 0,

    /// <summary>调用返回错误或抛出异常。</summary>
    Failed = 1,

    /// <summary>该节点超过单节点超时。</summary>
    TimedOut = 2,

    /// <summary>整轮预算耗尽，未开始或未完成。</summary>
    BudgetExceeded = 3,

    /// <summary>熔断打开被跳过（由总览服务在调用引擎前生成，引擎不会产出）。</summary>
    CircuitSkipped = 4
}

/// <summary>单个节点的执行结果。</summary>
public sealed record FanoutResult<T>(
    NodeId NodeId,
    FanoutStatus Status,
    T? Data,
    PanelError? Error,
    TimeSpan Elapsed,
    DateTimeOffset CompletedAt);

/// <summary>FanoutEngine 参数。</summary>
public sealed class FanoutOptions
{
    public int MaxConcurrency { get; init; } = 6;

    public TimeSpan PerTargetTimeout { get; init; } = TimeSpan.FromSeconds(6);

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(10);

    public int ChannelCapacity { get; init; } = 256;
}

/// <summary>带并发上限、单节点超时、整轮预算的流式并发执行引擎。</summary>
public interface IFanoutEngine
{
    /// <summary>
    /// 按完成顺序流式产出结果。除调用方取消外，每个 target 恰好产出 1 条结果
    /// （预算耗尽时未开始的 target 补 <see cref="FanoutStatus.BudgetExceeded"/>）。
    /// </summary>
    IAsyncEnumerable<FanoutResult<T>> RunAsync<T>(
        IReadOnlyList<FanoutTarget<T>> targets,
        FanoutOptions options,
        CancellationToken cancellationToken);
}
