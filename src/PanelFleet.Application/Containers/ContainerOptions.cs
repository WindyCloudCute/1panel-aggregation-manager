namespace PanelFleet.Application.Containers;

/// <summary>P6 容器模块配置。默认值即生产可用值；可由 Lead 后续绑定 <c>Containers</c> 配置节。</summary>
public sealed class ContainerOptions
{
    /// <summary>单节点 1Panel 调用超时（秒）。</summary>
    public int NodeTimeoutSeconds { get; set; } = 10;

    /// <summary>跨节点并发上限。</summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>每节点拉取容器/Compose 的条数上限。</summary>
    public int PerNodePageSize { get; set; } = 200;

    /// <summary>写操作提交后等待容器状态变化的预算（秒），超时只提示"已提交"。</summary>
    public int VerifyTimeoutSeconds { get; set; } = 3;

    /// <summary>单次批量操作允许的容器总数上限（防止误选）。</summary>
    public int MaxBatchContainers { get; set; } = 20;

    /// <summary>日志 tail 允许的最大行数。</summary>
    public int LogTailMax { get; set; } = 1000;

    public TimeSpan NodeTimeout => TimeSpan.FromSeconds(NodeTimeoutSeconds);

    public TimeSpan VerifyTimeout => TimeSpan.FromSeconds(VerifyTimeoutSeconds);

    public void Validate()
    {
        EnsureRange(NodeTimeoutSeconds, 3, 120, nameof(NodeTimeoutSeconds));
        EnsureRange(MaxConcurrency, 1, 16, nameof(MaxConcurrency));
        EnsureRange(PerNodePageSize, 20, 1000, nameof(PerNodePageSize));
        EnsureRange(VerifyTimeoutSeconds, 0, 30, nameof(VerifyTimeoutSeconds));
        EnsureRange(MaxBatchContainers, 1, 100, nameof(MaxBatchContainers));
        EnsureRange(LogTailMax, 10, 2000, nameof(LogTailMax));
    }

    private static void EnsureRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Containers:{name} 必须在 {min}..{max} 之间。");
        }
    }
}
