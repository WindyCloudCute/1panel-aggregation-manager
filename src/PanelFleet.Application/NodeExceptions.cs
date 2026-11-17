using PanelFleet.Domain.Model;

namespace PanelFleet.Application;

/// <summary>请求参数校验失败（400）。</summary>
public sealed class NodeValidationException : Exception
{
    public NodeValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("请求参数校验失败。")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>节点不存在（404）。</summary>
public sealed class NodeNotFoundException : Exception
{
    public NodeNotFoundException(NodeId id)
        : base($"节点 {id} 不存在。")
    {
        NodeId = id;
    }

    public NodeId NodeId { get; }
}

/// <summary>名称或地址冲突（409）。</summary>
public sealed class NodeConflictException : Exception
{
    public NodeConflictException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    public string Field { get; }
}

/// <summary>分组不存在（404）。</summary>
public sealed class NodeGroupNotFoundException : Exception
{
    public NodeGroupNotFoundException(Guid id)
        : base($"分组 {id} 不存在。")
    {
    }
}
