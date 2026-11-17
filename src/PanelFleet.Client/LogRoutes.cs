namespace PanelFleet.Client;

/// <summary>
/// P10 系统日志相关 1Panel v2.3.2 路由（面板/系统/网站；容器日志复用
/// <see cref="ContainerRoutes.ContainerLog"/> 的 SSE 流）。
/// <para>探测与契约见 docs/17-系统日志详细设计.md。</para>
/// </summary>
public static class LogRoutes
{
    /// <summary>面板操作日志（core）：POST 分页查询，body <c>{page,pageSize,...}</c>。</summary>
    public const string PanelOperation = "api/v2/core/logs/operation";

    /// <summary>面板登录日志（core）：POST 分页查询。</summary>
    public const string PanelLogin = "api/v2/core/logs/login";

    /// <summary>系统日志来源状态（agent）：GET，返回 journalctl 版本与关键字过滤能力。</summary>
    public const string SystemStatus = "api/v2/logs/system/status";

    /// <summary>系统日志可选文件/日期（agent）：GET，返回字符串数组。</summary>
    public const string SystemFiles = "api/v2/logs/system/files";

    /// <summary>系统日志运行中服务列表（agent）：GET，用于按服务过滤。</summary>
    public const string SystemServices = "api/v2/logs/system/services";

    /// <summary>系统日志读取（agent）：POST，body <c>{pageSize,cursor,startTime,endTime,keyword,priority,service}</c>。</summary>
    public const string SystemRead = "api/v2/logs/system/read";

    /// <summary>网站列表（agent）：POST 查询，用于把域名解析为网站 id。</summary>
    public const string WebsiteSearch = "api/v2/websites/search";

    /// <summary>网站访问/错误日志（agent）：POST，body <c>{id,logType:"access.log"|"error.log",page,pageSize}</c>。</summary>
    public const string WebsiteLog = "api/v2/websites/log/search";
}
