namespace PanelFleet.Domain.Model;

/// <summary>1Panel 能力位标志。</summary>
public static class PanelCapability
{
    public const int None = 0;

    public const int Dashboard = 1;

    public const int Websites = 1 << 1;

    public const int Apps = 1 << 2;

    public const int Containers = 1 << 3;

    public const int Databases = 1 << 4;

    public const int Cronjobs = 1 << 5;
}
