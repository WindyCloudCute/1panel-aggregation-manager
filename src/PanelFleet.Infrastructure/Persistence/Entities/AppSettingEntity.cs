namespace PanelFleet.Infrastructure.Persistence.Entities;

public sealed class AppSettingEntity
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;
}
