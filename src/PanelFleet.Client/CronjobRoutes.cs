namespace PanelFleet.Client;

/// <summary>1Panel v2 计划任务相关路由（P9）。</summary>
public static class CronjobRoutes
{
    /// <summary>任务列表（POST，body: page/pageSize/info/groupIDs/orderBy/order）。</summary>
    public const string Search = "api/v2/cronjobs/search";

    /// <summary>任务详情/编辑载荷（POST，body: {id}）。</summary>
    public const string Info = "api/v2/cronjobs/load/info";

    /// <summary>创建任务（POST，body: CronjobOperate）。</summary>
    public const string Create = "api/v2/cronjobs";

    /// <summary>更新任务（POST，body: CronjobOperate 含 id）。</summary>
    public const string Update = "api/v2/cronjobs/update";

    /// <summary>删除任务（POST，body: {ids,cleanData,cleanRemoteData}；不可回滚）。</summary>
    public const string Delete = "api/v2/cronjobs/del";

    /// <summary>启用/停用（POST，body: {id,status}，status=Enable/Disable）。</summary>
    public const string Status = "api/v2/cronjobs/status";

    /// <summary>立即执行（POST，body: {id}）。</summary>
    public const string Handle = "api/v2/cronjobs/handle";

    /// <summary>周期下次执行时间预览（POST，body: {spec}）。</summary>
    public const string Next = "api/v2/cronjobs/next";

    /// <summary>执行记录列表（POST，body: page/pageSize/cronjobID/startTime/endTime/status）。</summary>
    public const string RecordsSearch = "api/v2/cronjobs/search/records";

    /// <summary>执行记录日志（POST，body: {id}；data 为日志文本）。</summary>
    public const string RecordLog = "api/v2/cronjobs/records/log";

    /// <summary>脚本库下拉（GET；本轮不用于创建表单）。</summary>
    public const string ScriptOptions = "api/v2/cronjobs/script/options";

    /// <summary>1Panel 自定义分组（POST，body: {}；type=cronjob 过滤）。</summary>
    public const string GroupsSearch = "api/v2/groups/search";
}
