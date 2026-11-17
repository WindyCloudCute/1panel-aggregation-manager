namespace PanelFleet.Application.Cronjobs;

/// <summary>P9 计划任务模块配置（默认即生产可用值；后续可由 Lead 绑定 <c>Cronjobs</c> 配置节）。</summary>
public sealed class CronjobOptions
{
    /// <summary>单节点 1Panel 调用超时（秒）。</summary>
    public int NodeTimeoutSeconds { get; set; } = 10;

    /// <summary>跨节点并发上限。</summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>每节点拉取任务/记录的条数上限。</summary>
    public int PerNodePageSize { get; set; } = 200;

    /// <summary>单次批量操作允许的任务数上限（防止误选）。</summary>
    public int MaxBatchJobs { get; set; } = 20;

    /// <summary>记录日志返回的字节上限（超出截断）。</summary>
    public int MaxRecordLogBytes { get; set; } = 262144;

    /// <summary>执行记录查询默认天数。</summary>
    public int RecordDefaultDays { get; set; } = 7;

    public TimeSpan NodeTimeout => TimeSpan.FromSeconds(NodeTimeoutSeconds);

    public void Validate()
    {
        EnsureRange(NodeTimeoutSeconds, 3, 120, nameof(NodeTimeoutSeconds));
        EnsureRange(MaxConcurrency, 1, 16, nameof(MaxConcurrency));
        EnsureRange(PerNodePageSize, 20, 1000, nameof(PerNodePageSize));
        EnsureRange(MaxBatchJobs, 1, 200, nameof(MaxBatchJobs));
        EnsureRange(MaxRecordLogBytes, 4096, 4 * 1024 * 1024, nameof(MaxRecordLogBytes));
        EnsureRange(RecordDefaultDays, 1, 90, nameof(RecordDefaultDays));
    }

    private static void EnsureRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Cronjobs:{name} 必须在 {min}..{max} 之间。");
        }
    }
}
