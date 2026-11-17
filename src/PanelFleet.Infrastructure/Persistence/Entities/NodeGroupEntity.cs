namespace PanelFleet.Infrastructure.Persistence.Entities;

public sealed class NodeGroupEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public string CreatedAt { get; set; } = string.Empty;
}
