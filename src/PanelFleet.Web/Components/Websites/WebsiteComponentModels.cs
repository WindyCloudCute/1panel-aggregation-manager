namespace PanelFleet.Web.Components.Websites;

/// <summary>编辑网站基础信息表单结果。</summary>
public sealed record WebsiteEditResult(
    long WebsiteId,
    string PrimaryDomain,
    string? Remark,
    long GroupId,
    string ExpireDate,
    bool Favorite,
    bool IpV6);

/// <summary>新增域名表单结果。</summary>
public sealed record DomainCreateResult(long WebsiteId, string Domain, int Port, bool Ssl);

/// <summary>新增/编辑反向代理表单结果（Operate=create|edit）。</summary>
public sealed record ProxyEditResult(
    long WebsiteId,
    string Operate,
    string ProxyName,
    string Match,
    string ProxyPass,
    string ProxyHost,
    bool Enable,
    bool Cache,
    int CacheTime,
    string CacheUnit,
    int ServerCacheTime,
    string ServerCacheUnit,
    bool Sni,
    string? ProxySslName,
    bool SslVerify,
    bool Cors,
    string? AllowOrigins,
    string? AllowMethods,
    string? AllowHeaders,
    bool AllowCredentials,
    bool Preflight);

/// <summary>SSL 部署表单结果。</summary>
public sealed record SslDeployResult(long WebsiteId, long SslId, string HttpConfig);

/// <summary>建站向导结果。</summary>
public sealed record WebsiteCreateWizardResult(
    Guid NodeId,
    string Type,
    string PrimaryDomain,
    IReadOnlyList<string> OtherDomains,
    string? Alias,
    string? Remark,
    long GroupId,
    int Port,
    long RuntimeId,
    string? RuntimeType,
    string? Proxy,
    long ParentWebsiteId,
    string? SiteDir,
    bool IpV6);

/// <summary>SSL 证书申请表单结果。</summary>
public sealed record SslRequestResult(
    Guid NodeId,
    string PrimaryDomain,
    IReadOnlyList<string> OtherDomains,
    string Provider,
    long AcmeAccountId,
    long DnsAccountId,
    string? Description,
    bool AutoRenew,
    string KeyType);
