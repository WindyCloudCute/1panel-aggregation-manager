namespace PanelFleet.Client;

/// <summary>
/// P7 网站管理相关 1Panel v2 路由。
/// <para>读接口经真实 1Panel v2.3.2 只读探测确认；写接口按同版本源码/前端核对（docs/14）。</para>
/// </summary>
public static class WebsiteRoutes
{
    /// <summary>创建网站（POST，body 见 WebsiteCreate 契约；成功无 data，需按 taskID 轮询任务）。</summary>
    public const string Create = "api/v2/websites";

    /// <summary>建站预检查（POST，body: {installIds:[]}；返回非空表示存在阻断项）。</summary>
    public const string CreateCheck = "api/v2/websites/check";

    /// <summary>建站选项：已有网站下拉（POST，body: {types:[...]}）。</summary>
    public const string CreateOptions = "api/v2/websites/options";

    /// <summary>网站分页搜索（POST，orderBy/order 为必填参数）。</summary>
    public const string Search = "api/v2/websites/search";

    /// <summary>网站全量列表（GET）。</summary>
    public const string List = "api/v2/websites/list";

    /// <summary>1Panel 自定义分组（POST，body 需带 type：website）。</summary>
    public const string GroupsSearch = "api/v2/groups/search";

    /// <summary>更新网站基础信息（域名/备注/分组/到期/收藏）。</summary>
    public const string Update = "api/v2/websites/update";

    /// <summary>网站启停（body: {id, operate: start|stop}）。</summary>
    public const string Operate = "api/v2/websites/operate";

    /// <summary>新增域名（POST，body: {websiteID, domains:[{domain,port,ssl}]}）。</summary>
    public const string DomainCreate = "api/v2/websites/domains";

    /// <summary>更新域名 SSL 开关（POST，body: {id, ssl}）。</summary>
    public const string DomainUpdate = "api/v2/websites/domains/update";

    /// <summary>删除域名（POST，body: {id}）。</summary>
    public const string DomainDelete = "api/v2/websites/domains/del";

    /// <summary>查询反向代理列表（POST，body: {id: websiteId}）。</summary>
    public const string Proxies = "api/v2/websites/proxies";

    /// <summary>新增/编辑反向代理（POST，body 带 operate=create|edit）。</summary>
    public const string ProxyUpdate = "api/v2/websites/proxies/update";

    /// <summary>删除反向代理（POST，body: {id: websiteId, name}）。</summary>
    public const string ProxyDelete = "api/v2/websites/proxies/delete";

    /// <summary>启停反向代理（POST，body: {id: websiteId, name, status: enable|disable}）。</summary>
    public const string ProxyStatus = "api/v2/websites/proxies/status";

    /// <summary>SSL 证书列表（POST，body: {}；响应含私钥字段，客户端 DTO 不映射）。</summary>
    public const string SslList = "api/v2/websites/ssl/list";

    /// <summary>SSL 证书分页搜索（POST）。</summary>
    public const string SslSearch = "api/v2/websites/ssl/search";

    /// <summary>创建证书记录（POST，body 见 WebsiteSSLCreate；返回 data.id 后再调用申请接口）。</summary>
    public const string SslCreate = "api/v2/websites/ssl";

    /// <summary>删除证书（POST，body: {id}）。</summary>
    public const string SslDelete = "api/v2/websites/ssl/del";

    /// <summary>申请证书（POST，body: {ID: sslId}）。</summary>
    public const string SslObtain = "api/v2/websites/ssl/obtain";

    /// <summary>ACME 账户分页搜索（POST，body: {page,pageSize}；建站/申请证书时选择账户）。</summary>
    public const string AcmeSearch = "api/v2/websites/acme/search";

    /// <summary>运行环境列表（POST，body: {page,pageSize,type}；用于 PHP 版本切换）。</summary>
    public const string RuntimesSearch = "api/v2/runtimes/search";

    /// <summary>网站详情（GET）。</summary>
    public static string Detail(long websiteId) => $"api/v2/websites/{websiteId}";

    /// <summary>网站域名列表（GET）。</summary>
    public static string Domains(long websiteId) => $"api/v2/websites/domains/{websiteId}";

    /// <summary>网站 HTTPS 配置（GET 读取 / POST 更新，同一路径）。</summary>
    public static string Https(long websiteId) => $"api/v2/websites/{websiteId}/https";
}
