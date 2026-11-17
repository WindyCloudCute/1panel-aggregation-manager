namespace PanelFleet.Application.Resources;

/// <summary>P4 资源清单配置（绑定 Resources 节）。</summary>
public sealed class ResourceOptions
{
    public int CacheTtlSeconds { get; set; } = 30;

    public int DetailCacheTtlSeconds { get; set; } = 60;

    public int NodeTimeoutSeconds { get; set; } = 10;

    public int BudgetSeconds { get; set; } = 15;

    public int MaxConcurrency { get; set; } = 6;

    public int PerNodeLimit { get; set; } = 200;

    public int MergeLimit { get; set; } = 2000;

    public int RefreshIntervalSeconds { get; set; } = 60;

    public int AccessTtlSeconds { get; set; } = 600;

    public TimeSpan CacheTtl => TimeSpan.FromSeconds(CacheTtlSeconds);

    public TimeSpan DetailCacheTtl => TimeSpan.FromSeconds(DetailCacheTtlSeconds);

    public TimeSpan NodeTimeout => TimeSpan.FromSeconds(NodeTimeoutSeconds);

    public TimeSpan Budget => TimeSpan.FromSeconds(BudgetSeconds);

    public TimeSpan AccessTtl => TimeSpan.FromSeconds(AccessTtlSeconds);

    public void Validate()
    {
        EnsureRange(CacheTtlSeconds, 5, 600, nameof(CacheTtlSeconds));
        EnsureRange(DetailCacheTtlSeconds, 5, 600, nameof(DetailCacheTtlSeconds));
        EnsureRange(NodeTimeoutSeconds, 3, 60, nameof(NodeTimeoutSeconds));
        EnsureRange(BudgetSeconds, 5, 120, nameof(BudgetSeconds));
        EnsureRange(MaxConcurrency, 1, 16, nameof(MaxConcurrency));
        EnsureRange(PerNodeLimit, 20, 1000, nameof(PerNodeLimit));
        EnsureRange(MergeLimit, 100, 10000, nameof(MergeLimit));
        EnsureRange(AccessTtlSeconds, 60, 3600, nameof(AccessTtlSeconds));

        if (RefreshIntervalSeconds != 0 && RefreshIntervalSeconds is < 10 or > 3600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RefreshIntervalSeconds),
                RefreshIntervalSeconds,
                "Resources:RefreshIntervalSeconds 必须为 0（禁用）或 10..3600 秒。");
        }
    }

    private static void EnsureRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Resources:{name} 必须在 {min}..{max} 之间。");
        }
    }
}
