namespace PanelFleet.Application.Databases;

/// <summary>P8 数据库模块配置。默认值即生产可用值。</summary>
public sealed class DatabaseOptions
{
    /// <summary>单节点 1Panel 只读调用超时（秒）。一个节点要串行拉取多引擎/多实例。</summary>
    public int NodeTimeoutSeconds { get; set; } = 20;

    /// <summary>跨节点并发上限。</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>每实例库列表拉取条数上限。</summary>
    public int PerInstancePageSize { get; set; } = 200;

    public TimeSpan NodeTimeout => TimeSpan.FromSeconds(NodeTimeoutSeconds);

    public void Validate()
    {
        EnsureRange(NodeTimeoutSeconds, 3, 180, nameof(NodeTimeoutSeconds));
        EnsureRange(MaxConcurrency, 1, 16, nameof(MaxConcurrency));
        EnsureRange(PerInstancePageSize, 20, 1000, nameof(PerInstancePageSize));
    }

    private static void EnsureRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Databases:{name} 必须在 {min}..{max} 之间。");
        }
    }
}
