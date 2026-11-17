namespace PanelFleet.Web.Options;

public sealed class HealthOptions
{
    public int IntervalSeconds { get; set; } = 60;

    public int MaxConcurrency { get; set; } = 4;

    public int TimeoutSeconds { get; set; } = 8;
}
