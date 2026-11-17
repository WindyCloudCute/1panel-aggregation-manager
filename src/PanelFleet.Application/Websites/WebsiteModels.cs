using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Websites;

/// <summary>网站列表查询（单节点：管理操作需要明确的节点上下文）。</summary>
public sealed record WebsiteQuery(Guid? NodeId, long? GroupId, string? Name, int Page, int PageSize);

/// <summary>1Panel 网站分组。</summary>
public sealed record WebsiteGroupInfo(long Id, string Name, bool IsDefault);

/// <summary>网站列表行（含分组归属）。</summary>
public sealed record WebsiteRow(
    Guid NodeId,
    string NodeName,
    long Id,
    string PrimaryDomain,
    string? Alias,
    string? Type,
    string Status,
    string? Protocol,
    string? Remark,
    string? ExpireDate,
    string? SitePath,
    string? AppName,
    string? RuntimeName,
    string? RuntimeType,
    string? SslExpireDate,
    string? SslStatus,
    bool Favorite,
    long GroupId,
    string GroupName,
    IReadOnlyList<string> ChildSites,
    string? ParentSite);

/// <summary>网站列表结果（单节点，服务端在合并分组后做过滤/排序/分页）。</summary>
public sealed record WebsiteListResult(
    IReadOnlyList<WebsiteRow> Rows,
    int TotalMatched,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<WebsiteGroupInfo> Groups,
    bool Truncated,
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAt);

/// <summary>网站详情（安全字段，不含 SSL 私钥/编排内容）。</summary>
public sealed record WebsiteDetail(
    Guid NodeId,
    string NodeName,
    long Id,
    string PrimaryDomain,
    string? Alias,
    string? Type,
    string Status,
    string? Protocol,
    string? Remark,
    string? ExpireDate,
    string? CreatedAt,
    string? SitePath,
    string? SiteDir,
    string? AppName,
    long AppInstallId,
    long RuntimeId,
    string? RuntimeName,
    string? RuntimeType,
    long GroupId,
    string GroupName,
    bool Favorite,
    bool IPv6,
    bool DefaultServer,
    bool OpenBaseDir,
    bool AccessLogEnabled,
    bool ErrorLogEnabled,
    string? AccessLogPath,
    string? ErrorLogPath,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> ChildSites,
    string? ParentSite,
    DateTimeOffset FetchedAt);

/// <summary>网站域名。</summary>
public sealed record WebsiteDomain(long Id, long WebsiteId, string Domain, int Port, bool Ssl);

/// <summary>反向代理配置（不含 content 文件内容，避免大字段进入页面响应）。</summary>
public sealed record WebsiteProxy(
    long WebsiteId,
    string Name,
    string? Match,
    string? ProxyPass,
    string? ProxyHost,
    bool Enable,
    bool Cache,
    int CacheTime,
    string? CacheUnit,
    int ServerCacheTime,
    string? ServerCacheUnit,
    bool Sni,
    string? ProxySslName,
    bool SslVerify,
    bool Cors,
    string? AllowOrigins,
    string? AllowMethods,
    string? AllowHeaders,
    bool AllowCredentials,
    bool Preflight,
    string? FilePath);

/// <summary>SSL 证书安全元数据（不包含 privateKey / pem）。</summary>
public sealed record WebsiteSsl(
    long Id,
    string PrimaryDomain,
    string? Domains,
    string? Type,
    string? Provider,
    string? Organization,
    string Status,
    string? Message,
    string? ExpireDate,
    string? StartDate,
    bool AutoRenew,
    string? Description,
    bool IsIp,
    string? KeyType,
    string? CertPath,
    string? PrivateKeyPath,
    bool PushDir,
    string? Dir);

/// <summary>网站 HTTPS 配置。</summary>
public sealed record WebsiteHttpsConfig(
    bool Enable,
    string? HttpConfig,
    WebsiteSsl? Certificate,
    IReadOnlyList<string> SslProtocols,
    string? Algorithm,
    bool Hsts,
    bool HstsIncludeSubDomains,
    string? HttpsPort,
    IReadOnlyList<int> HttpsPorts,
    bool Http3);

/// <summary>PHP 运行环境（用于网站 PHP 版本切换）。</summary>
public sealed record WebsiteRuntime(long Id, string Name, string? Version, string Status, string? Type);

/// <summary>建站时可选择的已有网站（子网站父级等用途）。</summary>
public sealed record WebsiteOption(long Id, string PrimaryDomain, string? Alias);

/// <summary>建站预检查项；返回非空即存在阻断项，应先处理再创建。</summary>
public sealed record WebsitePrecheckIssue(string Name, string? Status, string? Version, string? AppName);

/// <summary>ACME 账户安全元数据（不含私钥）。</summary>
public sealed record WebsiteAcmeAccount(long Id, string Email, string? Type, bool IsDefault);

/// <summary>网站/SSL 查询失败（Web 层按 <see cref="Error"/> 映射状态码）。</summary>
public sealed class WebsiteException : Exception
{
    public WebsiteException(PanelError error)
        : base(error.Message) => Error = error;

    public WebsiteException(string message, PanelErrorKind kind = PanelErrorKind.Protocol)
        : base(message) => Error = new PanelError(kind, message);

    public PanelError Error { get; }
}
