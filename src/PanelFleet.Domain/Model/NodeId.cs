namespace PanelFleet.Domain.Model;

/// <summary>节点唯一标识。</summary>
public readonly record struct NodeId(Guid Value)
{
    public static NodeId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
