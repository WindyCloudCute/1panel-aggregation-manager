namespace PanelFleet.Application.Operations;

/// <summary>写操作内核配置。</summary>
public sealed class OperationOptions
{
    /// <summary>确认 Token 有效期（秒）。</summary>
    public int ConfirmationTtlSeconds { get; set; } = 120;

    /// <summary>单次批量操作的最大节点数。</summary>
    public int MaxBatchNodes { get; set; } = 50;

    /// <summary>节点写锁等待超时（秒）。</summary>
    public int NodeLockTimeoutSeconds { get; set; } = 5;

    /// <summary>单个节点执行超时预算（秒）。</summary>
    public int ExecuteTimeoutSeconds { get; set; } = 120;

    /// <summary>相同操作指纹去重窗口（秒）。</summary>
    public int DedupeWindowSeconds { get; set; } = 10;

    /// <summary>内存中保留的执行历史条数。</summary>
    public int HistoryCapacity { get; set; } = 200;

    public void Validate()
    {
        RequirePositive(ConfirmationTtlSeconds, nameof(ConfirmationTtlSeconds));
        RequirePositive(MaxBatchNodes, nameof(MaxBatchNodes));
        RequirePositive(NodeLockTimeoutSeconds, nameof(NodeLockTimeoutSeconds));
        RequirePositive(ExecuteTimeoutSeconds, nameof(ExecuteTimeoutSeconds));
        RequirePositive(DedupeWindowSeconds, nameof(DedupeWindowSeconds));
        RequirePositive(HistoryCapacity, nameof(HistoryCapacity));
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, $"Operations:{name} 必须大于 0。");
        }
    }
}
