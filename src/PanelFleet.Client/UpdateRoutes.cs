namespace PanelFleet.Client;

/// <summary>
/// 1Panel 面板版本检查与升级路由（1Panel v2.3.2 的 core 路由，挂载前缀为 <c>/api/v2/core</c>）。
/// <para>核心接口：</para>
/// <list type="bullet">
/// <item><description><c>POST settings/search/base</c>（只读查询）：返回 <c>data.systemVersion</c> 当前版本；</description></item>
/// <item><description><c>GET settings/upgrade</c>：返回可用版本与更新说明；</description></item>
/// <item><description><c>POST settings/upgrade</c>：提交升级（异步执行并重启面板，无任务 ID）。</description></item>
/// </list>
/// </summary>
public static class UpdateRoutes
{
    /// <summary>
    /// 当前面板版本：POST 只读查询，body 为 <c>{}</c>，返回 <c>data.systemVersion</c>。
    /// 该 POST 是查询语义、无副作用（1Panel 官方前端同款调用）；面板没有 GET 版本接口。
    /// </summary>
    public const string PanelSettingBase = "api/v2/core/settings/search/base";

    /// <summary>可用版本与更新说明（GET）。</summary>
    public const string UpgradeInfo = "api/v2/core/settings/upgrade";

    /// <summary>提交升级（POST，body <c>{"version":"v2.x.y"}</c>；异步执行并重启面板，无任务 ID）。</summary>
    public const string UpgradeExecute = "api/v2/core/settings/upgrade";

    /// <summary>升级前磁盘可用空间来源（agent GET，<c>currentInfo.diskData</c>）；升级后也用于健康检查。</summary>
    public static string HealthProbe => PanelRoutes.DashboardBase("all", "all");
}
