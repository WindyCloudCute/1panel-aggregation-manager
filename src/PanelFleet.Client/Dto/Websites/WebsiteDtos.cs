using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Websites;

// P7 网站管理 DTO：全部宽松解析（字段缺失/类型变化不抛异常）。
// 安全约束：SSL 证书的 privateKey / pem 等私钥字段不声明、不映射；
// 网站详情的 webSiteSSL 对象同理不映射，避免私钥进入应用层、日志或响应。

/// <summary>网站分页列表项（POST api/v2/websites/search → response.WebsiteRes）。</summary>
public sealed class WebsiteListItemDto
{
    public long Id { get; set; }

    public string? CreatedAt { get; set; }

    public string? Protocol { get; set; }

    public string? PrimaryDomain { get; set; }

    public string? Type { get; set; }

    public string? Alias { get; set; }

    public string? Remark { get; set; }

    public string? Status { get; set; }

    public string? ExpireDate { get; set; }

    public string? SitePath { get; set; }

    public string? AppName { get; set; }

    public string? RuntimeName { get; set; }

    public string? RuntimeType { get; set; }

    public string? SSLExpireDate { get; set; }

    public string? SSLStatus { get; set; }

    public long AppInstallID { get; set; }

    public List<string>? ChildSites { get; set; }

    public string? ParentSite { get; set; }

    public bool Favorite { get; set; }

    public bool IPV6 { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>网站详情（GET api/v2/websites/{id} → response.WebsiteDTO）。</summary>
public sealed class WebsiteDetailDto
{
    public long Id { get; set; }

    public string? CreatedAt { get; set; }

    public string? Protocol { get; set; }

    public string? PrimaryDomain { get; set; }

    public string? Type { get; set; }

    public string? Alias { get; set; }

    public string? Remark { get; set; }

    public string? Status { get; set; }

    public string? ExpireDate { get; set; }

    public string? SitePath { get; set; }

    public string? SiteDir { get; set; }

    public string? AppName { get; set; }

    public long AppInstallID { get; set; }

    public string? RuntimeName { get; set; }

    public string? RuntimeType { get; set; }

    public long RuntimeID { get; set; }

    public long WebSiteGroupID { get; set; }

    public bool Favorite { get; set; }

    public bool IPV6 { get; set; }

    public bool DefaultServer { get; set; }

    public bool OpenBaseDir { get; set; }

    public string? AccessLogPath { get; set; }

    public string? ErrorLogPath { get; set; }

    public bool AccessLog { get; set; }

    public bool ErrorLog { get; set; }

    public string? Algorithm { get; set; }

    public List<string>? ChildSites { get; set; }

    public string? ParentSite { get; set; }

    /// <summary>域名列表：1Panel 可能返回字符串数组或 {domain,port,ssl} 对象数组，统一用 JsonElement 宽松承载。</summary>
    public JsonElement? Domains { get; set; }

    // 有意不映射 webSiteSSL（可能含私钥 PEM）与 rewrite 等大字段。
}

/// <summary>1Panel 自定义分组（POST api/v2/groups/search，type=website）。</summary>
public sealed class WebsiteGroupDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public bool IsDefault { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>网站域名（GET api/v2/websites/domains/{websiteId}）。</summary>
public sealed class WebsiteDomainDto
{
    public long Id { get; set; }

    public long WebsiteId { get; set; }

    public string? Domain { get; set; }

    public int Port { get; set; }

    public bool SSL { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>反向代理配置（POST api/v2/websites/proxies → request.WebsiteProxyConfig）。</summary>
public sealed class WebsiteProxyDto
{
    public long Id { get; set; }

    public string? Operate { get; set; }

    public bool Enable { get; set; }

    public bool Cache { get; set; }

    public int CacheTime { get; set; }

    public string? CacheUnit { get; set; }

    public int ServerCacheTime { get; set; }

    public string? ServerCacheUnit { get; set; }

    public string? Name { get; set; }

    public string? Modifier { get; set; }

    public string? Match { get; set; }

    public string? ProxyPass { get; set; }

    public string? ProxyHost { get; set; }

    public string? FilePath { get; set; }

    public bool SNI { get; set; }

    public string? ProxySSLName { get; set; }

    public bool SSLVerify { get; set; }

    public bool Cors { get; set; }

    public string? AllowOrigins { get; set; }

    public string? AllowMethods { get; set; }

    public string? AllowHeaders { get; set; }

    public bool AllowCredentials { get; set; }

    public bool Preflight { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>SSL 证书安全元数据（POST api/v2/websites/ssl/list/search）。私钥/PEM 有意不映射。</summary>
public sealed class WebsiteSslDto
{
    public long Id { get; set; }

    public string? CreatedAt { get; set; }

    public string? PrimaryDomain { get; set; }

    public string? Domains { get; set; }

    public string? Type { get; set; }

    public string? Provider { get; set; }

    public string? Organization { get; set; }

    public string? Status { get; set; }

    public string? Message { get; set; }

    public string? ExpireDate { get; set; }

    public string? StartDate { get; set; }

    public bool AutoRenew { get; set; }

    public string? Description { get; set; }

    public bool IsIP { get; set; }

    public string? KeyType { get; set; }

    public string? CertPath { get; set; }

    public string? PrivateKeyPath { get; set; }

    public bool PushDir { get; set; }

    public string? Dir { get; set; }

    public string? LogPath { get; set; }

    // 安全约束：不声明 [JsonExtensionData]，避免 1Panel 返回的 privateKey / pem 等私钥字段
    // 被扩展数据字典捕获后再次序列化（审计/日志/接口响应均不得出现证书私钥）。
}

/// <summary>HTTPS 配置（GET/POST api/v2/websites/{id}/https）。</summary>
public sealed class WebsiteHttpsDto
{
    public bool Enable { get; set; }

    public string? HttpConfig { get; set; }

    public WebsiteSslDto? SSL { get; set; }

    public List<string>? SSLProtocol { get; set; }

    public string? Algorithm { get; set; }

    public bool Hsts { get; set; }

    public bool HstsIncludeSubDomains { get; set; }

    public string? HttpsPort { get; set; }

    public List<int>? HttpsPorts { get; set; }

    public bool Http3 { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>运行环境列表项（POST api/v2/runtimes/search，type=php）。</summary>
public sealed class WebsiteRuntimeDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public string? Status { get; set; }

    public string? Resource { get; set; }

    public long AppDetailID { get; set; }

    public long AppID { get; set; }

    public string? Image { get; set; }

    public string? Message { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>建站选项（POST api/v2/websites/options）。</summary>
public sealed class WebsiteOptionDto
{
    public long Id { get; set; }

    public string? PrimaryDomain { get; set; }

    public string? Alias { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>建站预检查项（POST api/v2/websites/check；非空表示存在阻断项）。</summary>
public sealed class WebsitePrecheckItemDto
{
    public string? Name { get; set; }

    public string? Status { get; set; }

    public string? Version { get; set; }

    public string? AppName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>ACME 账户安全元数据（POST api/v2/websites/acme/search）。</summary>
public sealed class WebsiteAcmeAccountDto
{
    public long Id { get; set; }

    public string? Email { get; set; }

    public string? Type { get; set; }

    public string? Status { get; set; }

    public bool IsDefault { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>网站搜索请求体（orderBy/order 为 1Panel 必填参数）。</summary>
public sealed record WebsiteSearchRequest(
    int Page,
    int PageSize,
    string? Name,
    string OrderBy,
    string Order,
    long WebsiteGroupId,
    string? Type);

/// <summary>分组查询请求体（type 为 1Panel 必填参数）。</summary>
public sealed record WebsiteGroupSearchRequest(string Type);

/// <summary>运行环境查询请求体。</summary>
public sealed record WebsiteRuntimeSearchRequest(int Page, int PageSize, string? Type);
