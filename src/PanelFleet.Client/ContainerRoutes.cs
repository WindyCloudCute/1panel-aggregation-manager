namespace PanelFleet.Client;

/// <summary>
/// P6 容器与 Compose 相关 1Panel v2 路由。
/// <para>全部路径经过真实 1Panel v2.3.2 节点探测确认；请求/响应字段见 docs/11。</para>
/// </summary>
public static class ContainerRoutes
{
    public const string Search = "api/v2/containers/search";

    public const string Info = "api/v2/containers/info";

    public const string Inspect = "api/v2/containers/inspect";

    public const string Operate = "api/v2/containers/operate";

    public const string Rename = "api/v2/containers/rename";

    public const string Status = "api/v2/containers/status";

    public const string ListStats = "api/v2/containers/list/stats";

    public const string ItemStats = "api/v2/containers/item/stats";

    public const string ComposeSearch = "api/v2/containers/compose/search";

    public const string ComposeOperate = "api/v2/containers/compose/operate";

    /// <summary>创建并启动 Compose 项目（POST api/v2/containers/compose，异步任务）。</summary>
    public const string ComposeCreate = "api/v2/containers/compose";

    /// <summary>仅校验 Compose 配置（POST api/v2/containers/compose/test）。</summary>
    public const string ComposeTest = "api/v2/containers/compose/test";

    /// <summary>更新 Compose 内容并重建（POST api/v2/containers/compose/update，异步任务）。</summary>
    public const string ComposeUpdate = "api/v2/containers/compose/update";

    /// <summary>读取 Compose 项目 .env 内容（POST api/v2/containers/compose/env）。</summary>
    public const string ComposeEnv = "api/v2/containers/compose/env";

    /// <summary>只读读取单个文件（POST api/v2/files/content，body { path, isDetail }）。1Panel v2.3.2 agent 路由。</summary>
    public const string FileContent = "api/v2/files/content";

    public const string ImageSearch = "api/v2/containers/image/search";

    /// <summary>单容器实时统计：GET api/v2/containers/stats/{容器 ID 或名称}。</summary>
    public static string Stats(string containerIdOrName) =>
        $"api/v2/containers/stats/{Uri.EscapeDataString(containerIdOrName)}";

    /// <summary>
    /// 容器/Compose 日志 SSE 流（GET，需 <c>Accept: text/event-stream</c>）。
    /// <para>follow=false 时 1Panel 在 docker logs 输出完毕后关闭流，用于一次性 tail 查看。</para>
    /// </summary>
    public static string ContainerLog(
        string? container,
        string? compose,
        string since,
        int tail,
        bool follow,
        bool timestamp)
    {
        var query = new List<string>(6);
        if (!string.IsNullOrEmpty(compose))
        {
            query.Add("compose=" + Uri.EscapeDataString(compose));
        }
        else
        {
            query.Add("container=" + Uri.EscapeDataString(container ?? string.Empty));
        }

        query.Add("since=" + Uri.EscapeDataString(string.IsNullOrEmpty(since) ? "all" : since));
        query.Add("tail=" + tail.ToString(System.Globalization.CultureInfo.InvariantCulture));
        query.Add("follow=" + (follow ? "true" : "false"));
        query.Add("timestamp=" + (timestamp ? "true" : "false"));

        return "api/v2/containers/search/log?" + string.Join("&", query);
    }
}
