namespace PanelFleet.Client;

/// <summary>1Panel v2 路由常量。所有路径集中在此处维护，新增路径必须补契约测试。</summary>
public static class PanelRoutes
{
    public const string DashboardOs = "api/v2/dashboard/base/os";

    public const string DashboardCurrentNode = "api/v2/dashboard/current/node";

    public const string SettingsSearch = "api/v2/settings/search";

    public static string DashboardBase(string ioOption, string netOption) =>
        $"api/v2/dashboard/base/{Uri.EscapeDataString(ioOption)}/{Uri.EscapeDataString(netOption)}";

    public static string DashboardCurrent(string ioOption, string netOption) =>
        $"api/v2/dashboard/current/{Uri.EscapeDataString(ioOption)}/{Uri.EscapeDataString(netOption)}";
}
