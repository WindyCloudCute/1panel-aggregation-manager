namespace PanelFleet.Client;

/// <summary>
/// P5 应用商店相关 1Panel v2 路由。所有路径集中在此处维护，新增路径必须补契约测试。
/// </summary>
public static class AppStoreRoutes
{
    /// <summary>应用商店应用搜索（POST）。</summary>
    public const string Search = "api/v2/apps/search";

    /// <summary>应用商店标签列表（GET）。</summary>
    public const string Tags = "api/v2/apps/tags";

    /// <summary>应用商店是否有更新（GET）。</summary>
    public const string CheckUpdate = "api/v2/apps/checkupdate";

    /// <summary>安装应用（POST）。</summary>
    public const string Install = "api/v2/apps/install";

    /// <summary>已安装应用列表（POST）。</summary>
    public const string InstalledSearch = "api/v2/apps/installed/search";

    /// <summary>已安装应用操作：start/stop/restart/delete/upgrade 等（POST）。</summary>
    public const string InstalledOperate = "api/v2/apps/installed/op";

    /// <summary>修改已安装应用参数（POST）。</summary>
    public const string InstalledParamsUpdate = "api/v2/apps/installed/params/update";

    /// <summary>查询已安装应用的可用升级版本（POST）。</summary>
    public const string InstalledUpdateVersions = "api/v2/apps/installed/update/versions";

    /// <summary>1Panel 异步任务查询（POST，位于 logs 路由组）。</summary>
    public const string TaskSearch = "api/v2/logs/tasks/search";

    /// <summary>按应用 key 获取应用信息（版本列表、标签等）。</summary>
    public static string App(string appKey) => $"api/v2/apps/{Uri.EscapeDataString(appKey)}";

    /// <summary>按应用 ID + 版本获取应用版本详情（安装参数表单）。1Panel 固定使用 <c>app</c> 类型。</summary>
    public static string AppDetail(long appId, string version) =>
        $"api/v2/apps/detail/{appId}/{Uri.EscapeDataString(version)}/app";

    /// <summary>已安装应用详情。</summary>
    public static string InstalledInfo(long installId) => $"api/v2/apps/installed/info/{installId}";

    /// <summary>已安装应用参数（端口、密码等安装参数当前值）。</summary>
    public static string InstalledParams(long installId) => $"api/v2/apps/installed/params/{installId}";

    /// <summary>卸载前关联资源检查（关联数据库/网站等）。</summary>
    public static string InstalledDeleteCheck(long installId) => $"api/v2/apps/installed/delete/check/{installId}";
}
