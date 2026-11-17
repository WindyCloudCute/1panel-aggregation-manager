namespace PanelFleet.Application.Overview;

/// <summary>聚合总览配置（绑定 Overview 节）。</summary>
public sealed class OverviewOptions
{
    /// <summary>后台整轮刷新间隔（秒）。0 或负数 = 禁用自动刷新；合法范围 10..3600。</summary>
    public int RefreshIntervalSeconds { get; set; } = 30;

    /// <summary>页面打开时缓存新鲜阈值（秒）。</summary>
    public int CacheTtlSeconds { get; set; } = 25;

    /// <summary>指标过期阈值（秒，超过即标记 Stale）。</summary>
    public int StaleAfterSeconds { get; set; } = 90;

    /// <summary>聚合并发上限。</summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>单节点超时（秒）。</summary>
    public int NodeTimeoutSeconds { get; set; } = 6;

    /// <summary>整轮预算（秒）。</summary>
    public int BudgetSeconds { get; set; } = 10;

    public CircuitOptions Circuit { get; set; } = new();

    public TimeSpan NodeTimeout => TimeSpan.FromSeconds(NodeTimeoutSeconds);

    public TimeSpan Budget => TimeSpan.FromSeconds(BudgetSeconds);

    public TimeSpan StaleAfter => TimeSpan.FromSeconds(StaleAfterSeconds);

    public TimeSpan CacheTtl => TimeSpan.FromSeconds(CacheTtlSeconds);

    /// <summary>启动时 fail-closed 校验；任何越界都抛异常，不做静默 clamp。</summary>
    public void Validate()
    {
        if (RefreshIntervalSeconds == 0 || RefreshIntervalSeconds < 0)
        {
            // 显式禁用自动刷新。
        }
        else if (RefreshIntervalSeconds is < 10 or > 3600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RefreshIntervalSeconds),
                RefreshIntervalSeconds,
                "Overview:RefreshIntervalSeconds 必须为 0（禁用）或 10..3600 秒。");
        }

        EnsureRange(CacheTtlSeconds, 5, 600, nameof(CacheTtlSeconds));
        EnsureRange(StaleAfterSeconds, 30, 3600, nameof(StaleAfterSeconds));
        EnsureRange(MaxConcurrency, 1, 16, nameof(MaxConcurrency));
        EnsureRange(NodeTimeoutSeconds, 2, 30, nameof(NodeTimeoutSeconds));
        EnsureRange(BudgetSeconds, 3, 60, nameof(BudgetSeconds));

        Circuit.Validate();
    }

    private static void EnsureRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"Overview:{name} 必须在 {min}..{max} 之间。");
        }
    }
}

/// <summary>熔断配置。</summary>
public sealed class CircuitOptions
{
    /// <summary>连续失败多少次后打开熔断。</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>熔断持续时长（秒）。</summary>
    public int BreakSeconds { get; set; } = 30;

    public TimeSpan BreakDuration => TimeSpan.FromSeconds(BreakSeconds);

    internal void Validate()
    {
        if (FailureThreshold is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FailureThreshold),
                FailureThreshold,
                "Overview:Circuit:FailureThreshold 必须在 1..10 之间。");
        }

        if (BreakSeconds is < 5 or > 600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BreakSeconds),
                BreakSeconds,
                "Overview:Circuit:BreakSeconds 必须在 5..600 之间。");
        }
    }
}
