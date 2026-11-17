namespace PanelFleet.Application.Logs;

/// <summary>P10 系统日志模块配置（默认值即生产可用值；接线处可绑定 <c>Logs</c> 配置节覆盖）。</summary>
public sealed class LogOptions
{
    /// <summary>单节点日志调用超时（秒）。</summary>
    public int NodeTimeoutSeconds { get; set; } = 15;

    /// <summary>跨节点并发上限。</summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>单节点单次查询返回的最大行数（同时作为 limit 上限）。</summary>
    public int MaxLines { get; set; } = 2000;

    /// <summary>单节点单次查询返回内容的最大字节数（UTF-8，约 1 MiB）。</summary>
    public long MaxBytes { get; set; } = 1024 * 1024;

    /// <summary>整轮多节点查询预算（秒）。</summary>
    public int BudgetSeconds { get; set; } = 25;

    public TimeSpan NodeTimeout => TimeSpan.FromSeconds(NodeTimeoutSeconds);

    public TimeSpan Budget => TimeSpan.FromSeconds(BudgetSeconds);

    public void Validate()
    {
        EnsureRange(NodeTimeoutSeconds, 3, 120, nameof(NodeTimeoutSeconds));
        EnsureRange(MaxConcurrency, 1, 16, nameof(MaxConcurrency));
        EnsureRange(MaxLines, 10, 2000, nameof(MaxLines));
        EnsureRange(BudgetSeconds, 5, 120, nameof(BudgetSeconds));

        if (MaxBytes < 64 * 1024 || MaxBytes > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBytes),
                MaxBytes,
                "Logs:MaxBytes 必须在 64 KiB..16 MiB 之间。");
        }
    }

    private static void EnsureRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Logs:{name} 必须在 {min}..{max} 之间。");
        }
    }
}
