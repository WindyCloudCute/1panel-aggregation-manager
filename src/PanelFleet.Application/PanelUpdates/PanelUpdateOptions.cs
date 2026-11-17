namespace PanelFleet.Application.PanelUpdates;

/// <summary>P12 面板更新模块配置（默认值适用于单节点/小批量场景，可由接线处覆盖）。</summary>
public sealed class PanelUpdateOptions
{
    /// <summary>单节点一次版本检查（在线探测 + 当前版本 + 可用版本）的总超时（秒）。</summary>
    public int CheckTimeoutSeconds { get; set; } = 30;

    /// <summary>提交升级后等待面板恢复健康并回读版本的总超时（秒）。</summary>
    /// <remarks>写内核的单节点执行预算默认 120 秒，本值需留出请求开销余量，默认 90 秒。</remarks>
    public int HealthCheckTimeoutSeconds { get; set; } = 90;

    /// <summary>升级后健康检查/版本回读的轮询间隔（秒）。</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// 升级前要求的磁盘最小可用空间（字节）。1Panel v2.3.2 升级程序硬性要求安装目录可用空间 ≥ 500 MiB，
    /// 这里用节点磁盘信息估算；无法读取磁盘信息时只提示、不阻断。
    /// </summary>
    public long MinimumFreeDiskBytes { get; set; } = 500L * 1024 * 1024;

    public void Validate()
    {
        RequirePositive(CheckTimeoutSeconds, nameof(CheckTimeoutSeconds));
        RequirePositive(HealthCheckTimeoutSeconds, nameof(HealthCheckTimeoutSeconds));
        RequirePositive(HealthCheckIntervalSeconds, nameof(HealthCheckIntervalSeconds));

        if (MinimumFreeDiskBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumFreeDiskBytes),
                "PanelUpdate:MinimumFreeDiskBytes 必须大于 0。");
        }
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, $"PanelUpdate:{name} 必须大于 0。");
        }
    }
}
