namespace PanelFleet.Client;

/// <summary>P4 五类资源相关路由。</summary>
public static class ResourceRoutes
{
    public const string WebsitesSearch = "api/v2/websites/search";

    public const string AppsInstalledSearch = "api/v2/apps/installed/search";

    public const string ContainersSearch = "api/v2/containers/search";

    public const string ContainerInfo = "api/v2/containers/info";

    public const string CronjobsSearch = "api/v2/cronjobs/search";

    public const string CronjobInfo = "api/v2/cronjobs/load/info";

    /// <summary>1Panel 自定义分组列表（body 为空对象；返回项含 type：website/cronjob/host 等）。</summary>
    public const string GroupsSearch = "api/v2/groups/search";

    public static string WebsiteDetail(long id) => $"api/v2/websites/{id}";

    public static string AppDetail(long id) => $"api/v2/apps/installed/info/{id}";

    public static string DatabaseInstances(string engine) => $"api/v2/databases/db/list/{engine}";

    public static string DatabaseSearch(string engine) => engine switch
    {
        "mysql" => "api/v2/databases/search",
        "postgresql" => "api/v2/databases/pg/search",
        "mongodb" => "api/v2/databases/mongodb/search",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "未知数据库引擎。")
    };
}
