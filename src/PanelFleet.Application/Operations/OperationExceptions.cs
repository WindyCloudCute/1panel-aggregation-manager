namespace PanelFleet.Application.Operations;

/// <summary>写操作参数校验失败（Web 层返回 400）。</summary>
public sealed class OperationValidationException : Exception
{
    public OperationValidationException(string message)
        : base(message)
    {
    }

    public static OperationValidationException Missing(string label) => new($"缺少参数：{label}");
}

/// <summary>确认 Token 无效、确认文本不匹配或重复提交（Web 层返回 409）。</summary>
public sealed class OperationConfirmationException : Exception
{
    public OperationConfirmationException(string message, string reason)
        : base(message) => Reason = reason;

    public string Reason { get; }
}

/// <summary>未注册的操作类型（Web 层返回 400）。</summary>
public sealed class OperationNotSupportedException : Exception
{
    public OperationNotSupportedException(string kind)
        : base($"不支持的操作类型：{kind}")
    {
    }
}

/// <summary>节点正在执行其他写操作（节点写锁获取失败）。</summary>
public sealed class OperationBusyException : Exception
{
    public OperationBusyException(string nodeName)
        : base($"节点 {nodeName} 正在执行其他写操作，请稍后重试。")
    {
    }
}
