using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Client.Dto.Websites;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Websites;

/// <summary>
/// P7 网站管理只读查询（scoped）：列表/分组、详情、域名、反向代理、HTTPS/SSL、PHP 运行环境。
/// <para>写操作由 <c>IOperationHandler</c> 执行（website.* / domain.* / proxy.* / ssl.*）。</para>
/// </summary>
public sealed class WebsiteService
{
    /// <summary>单分组最多拉取的网站数（服务端合并/过滤/分页前的上限）。</summary>
    public const int PerGroupLimit = 200;

    /// <summary>单节点合并后的最大网站行数（防止超大节点撑爆页面）。</summary>
    public const int MaxRows = 1000;

    private const string DefaultGroupName = "默认分组";

    private const string UngroupedName = "未分组";

    private readonly INodeStore _nodes;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly ILogger<WebsiteService> _logger;

    public WebsiteService(
        INodeStore nodes,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        ILogger<WebsiteService> logger)
    {
        _nodes = nodes;
        _protector = protector;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>网站列表：按分组逐个查询以取得分组归属，合并后按名称过滤、收藏优先排序并分页。</summary>
    public async Task<WebsiteListResult> ListAsync(WebsiteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var record = await ResolveNodeAsync(query.NodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var warnings = new List<string>();
        var groups = await LoadGroupsAsync(client, warnings, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var targetGroups = query.GroupId is { } groupId
            ? groups.Where(group => group.Id == groupId).ToArray()
            : groups;

        var rows = new List<WebsiteRow>();
        var total = 0;
        var truncated = false;

        if (targetGroups.Count == 0)
        {
            // 无分组（或分组接口不可用/筛选分组不存在）：退化为不带分组的全量查询。
            var (fallbackRows, fallbackTotal, fallbackTruncated) = await SearchGroupAsync(
                client,
                record,
                query.Name,
                websiteGroupId: null,
                groupName: groups.Count == 0 ? UngroupedName : DefaultGroupName,
                cancellationToken).ConfigureAwait(false);
            rows.AddRange(fallbackRows);
            total += fallbackTotal;
            truncated |= fallbackTruncated;
        }
        else
        {
            foreach (var group in targetGroups)
            {
                var (groupRows, groupTotal, groupTruncated) = await SearchGroupAsync(
                    client, record, query.Name, group.Id, group.Name, cancellationToken).ConfigureAwait(false);
                rows.AddRange(groupRows);
                total += groupTotal;
                truncated |= groupTruncated;

                if (rows.Count >= MaxRows)
                {
                    truncated = true;
                    break;
                }
            }
        }

        if (rows.Count > MaxRows)
        {
            rows = rows.Take(MaxRows).ToList();
            truncated = true;
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var filter = query.Name.Trim();
            rows = rows
                .Where(row =>
                    row.PrimaryDomain.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (row.Alias?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
                    || (row.Remark?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                .ToList();
        }

        rows = rows
            .OrderByDescending(row => row.Favorite)
            .ThenBy(row => row.PrimaryDomain, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pageSize = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, 200);
        var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize));
        var page = Math.Clamp(query.Page <= 0 ? 1 : query.Page, 1, totalPages);
        var pageRows = rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return new WebsiteListResult(
            pageRows,
            rows.Count,
            page,
            pageSize,
            totalPages,
            groups,
            truncated || total > rows.Count,
            warnings,
            now);
    }

    /// <summary>网站分组列表（UI 分组树）。</summary>
    public async Task<IReadOnlyList<WebsiteGroupInfo>> GetGroupsAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));
        var warnings = new List<string>();
        return await LoadGroupsAsync(client, warnings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>网站详情。</summary>
    public async Task<WebsiteDetail> GetDetailAsync(Guid? nodeId, long websiteId, CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var warnings = new List<string>();
        var groups = await LoadGroupsAsync(client, warnings, cancellationToken).ConfigureAwait(false);

        var result = await client
            .GetAsync<WebsiteDetailDto>(WebsiteRoutes.Detail(websiteId), cancellationToken)
            .ConfigureAwait(false);
        var data = EnsureSuccess(result)
            ?? throw new WebsiteException($"网站 {websiteId} 不存在。", PanelErrorKind.NotFound);

        return new WebsiteDetail(
            record.Id.Value,
            record.Name,
            data.Id,
            Text(data.PrimaryDomain) ?? $"#{websiteId}",
            Text(data.Alias),
            Text(data.Type),
            Text(data.Status) ?? "unknown",
            Text(data.Protocol),
            Text(data.Remark),
            Text(data.ExpireDate),
            Text(data.CreatedAt),
            Text(data.SitePath),
            Text(data.SiteDir),
            Text(data.AppName),
            data.AppInstallID,
            data.RuntimeID,
            Text(data.RuntimeName),
            Text(data.RuntimeType),
            data.WebSiteGroupID,
            ResolveGroupName(groups, data.WebSiteGroupID),
            data.Favorite,
            data.IPV6,
            data.DefaultServer,
            data.OpenBaseDir,
            data.AccessLog,
            data.ErrorLog,
            Text(data.AccessLogPath),
            Text(data.ErrorLogPath),
            ReadDomains(data.Domains),
            data.ChildSites ?? [],
            Text(data.ParentSite),
            DateTimeOffset.UtcNow);
    }

    /// <summary>网站域名列表。</summary>
    public async Task<IReadOnlyList<WebsiteDomain>> GetDomainsAsync(
        Guid? nodeId,
        long websiteId,
        CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .GetAsync<List<WebsiteDomainDto>>(WebsiteRoutes.Domains(websiteId), cancellationToken)
            .ConfigureAwait(false);
        var items = EnsureSuccess(result) ?? [];

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Domain))
            .Select(item => new WebsiteDomain(item.Id, item.WebsiteId, item.Domain!.Trim(), item.Port, item.SSL))
            .ToArray();
    }

    /// <summary>反向代理列表。</summary>
    public async Task<IReadOnlyList<WebsiteProxy>> GetProxiesAsync(
        Guid? nodeId,
        long websiteId,
        CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .PostAsync<List<WebsiteProxyDto>>(WebsiteRoutes.Proxies, new { id = websiteId }, cancellationToken)
            .ConfigureAwait(false);
        var items = EnsureSuccess(result) ?? [];

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new WebsiteProxy(
                item.Id,
                item.Name!.Trim(),
                Text(item.Match),
                Text(item.ProxyPass),
                Text(item.ProxyHost),
                item.Enable,
                item.Cache,
                item.CacheTime,
                Text(item.CacheUnit),
                item.ServerCacheTime,
                Text(item.ServerCacheUnit),
                item.SNI,
                Text(item.ProxySSLName),
                item.SSLVerify,
                item.Cors,
                Text(item.AllowOrigins),
                Text(item.AllowMethods),
                Text(item.AllowHeaders),
                item.AllowCredentials,
                item.Preflight,
                Text(item.FilePath)))
            .ToArray();
    }

    /// <summary>网站 HTTPS 配置（含已绑定证书的安全元数据）。</summary>
    public async Task<WebsiteHttpsConfig> GetHttpsAsync(
        Guid? nodeId,
        long websiteId,
        CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .GetAsync<WebsiteHttpsDto>(WebsiteRoutes.Https(websiteId), cancellationToken)
            .ConfigureAwait(false);
        var data = EnsureSuccess(result);
        if (data is null)
        {
            return new WebsiteHttpsConfig(false, null, null, [], null, false, false, null, [], false);
        }

        return new WebsiteHttpsConfig(
            data.Enable,
            Text(data.HttpConfig),
            MapSsl(data.SSL),
            data.SSLProtocol ?? [],
            Text(data.Algorithm),
            data.Hsts,
            data.HstsIncludeSubDomains,
            Text(data.HttpsPort),
            data.HttpsPorts ?? [],
            data.Http3);
    }

    /// <summary>节点 SSL 证书列表（仅安全元数据，不含私钥/PEM）。</summary>
    public async Task<IReadOnlyList<WebsiteSsl>> GetSslCertificatesAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .PostAsync<List<WebsiteSslDto>>(WebsiteRoutes.SslList, new { }, cancellationToken)
            .ConfigureAwait(false);
        var items = EnsureSuccess(result) ?? [];

        return items
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.PrimaryDomain))
            .Select(MapSsl)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }

    /// <summary>PHP 运行环境列表（用于网站 PHP 版本切换）。</summary>
    public async Task<IReadOnlyList<WebsiteRuntime>> GetPhpRuntimesAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .PostAsync<PagedItemsDto<WebsiteRuntimeDto>>(
                WebsiteRoutes.RuntimesSearch,
                new WebsiteRuntimeSearchRequest(1, 200, "php"),
                cancellationToken)
            .ConfigureAwait(false);
        var data = EnsureSuccess(result);

        return (data?.Items ?? [])
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new WebsiteRuntime(
                item.Id,
                item.Name!.Trim(),
                ParsePhpVersion(item.Name, item.Image),
                Text(item.Status) ?? "unknown",
                Text(item.Type)))
            .ToArray();
    }

    /// <summary>建站选项：可作为子网站父级的已有网站（只读）。</summary>
    public async Task<IReadOnlyList<WebsiteOption>> GetCreateOptionsAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .PostAsync<List<WebsiteOptionDto>>(
                WebsiteRoutes.CreateOptions,
                new { types = new[] { "static", "runtime" } },
                cancellationToken)
            .ConfigureAwait(false);
        var items = EnsureSuccess(result) ?? [];

        return items
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.PrimaryDomain))
            .Select(item => new WebsiteOption(item.Id, item.PrimaryDomain!.Trim(), Text(item.Alias)))
            .ToArray();
    }

    /// <summary>建站预检查（只读）：返回非空表示存在阻断项，应在创建前处理。</summary>
    public async Task<IReadOnlyList<WebsitePrecheckIssue>> PrecheckAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .PostAsync<List<WebsitePrecheckItemDto>>(
                WebsiteRoutes.CreateCheck,
                new { installIds = Array.Empty<long>() },
                cancellationToken)
            .ConfigureAwait(false);
        var items = EnsureSuccess(result) ?? [];

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new WebsitePrecheckIssue(
                item.Name!.Trim(),
                Text(item.Status),
                Text(item.Version),
                Text(item.AppName)))
            .ToArray();
    }

    /// <summary>ACME 账户列表（只读，申请证书时选择；不包含任何私钥）。</summary>
    public async Task<IReadOnlyList<WebsiteAcmeAccount>> GetAcmeAccountsAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var result = await client
            .PostAsync<PagedItemsDto<WebsiteAcmeAccountDto>>(
                WebsiteRoutes.AcmeSearch,
                new { page = 1, pageSize = 100 },
                cancellationToken)
            .ConfigureAwait(false);
        var data = EnsureSuccess(result);

        return (data?.Items ?? [])
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Email))
            .Select(item => new WebsiteAcmeAccount(item.Id, item.Email!.Trim(), Text(item.Type), item.IsDefault))
            .ToArray();
    }

    private async Task<(IReadOnlyList<WebsiteRow> Rows, int Total, bool Truncated)> SearchGroupAsync(
        IPanelClient client,
        NodeRecord record,
        string? name,
        long? websiteGroupId,
        string groupName,
        CancellationToken cancellationToken)
    {
        var request = new WebsiteSearchRequest(
            1,
            PerGroupLimit,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            "created_at",
            "descending",
            websiteGroupId ?? 0,
            null);

        var result = await client
            .PostAsync<PagedItemsDto<WebsiteListItemDto>>(WebsiteRoutes.Search, request, cancellationToken)
            .ConfigureAwait(false);
        var data = EnsureSuccess(result);
        if (data is null)
        {
            return ([], 0, false);
        }

        var items = data.Items ?? [];
        var rows = items
            .Where(item => item.Id > 0)
            .Select(item => new WebsiteRow(
                record.Id.Value,
                record.Name,
                item.Id,
                Text(item.PrimaryDomain) ?? $"#{item.Id}",
                Text(item.Alias),
                Text(item.Type),
                Text(item.Status) ?? "unknown",
                Text(item.Protocol),
                Text(item.Remark),
                Text(item.ExpireDate),
                Text(item.SitePath),
                Text(item.AppName),
                Text(item.RuntimeName),
                Text(item.RuntimeType),
                Text(item.SSLExpireDate),
                Text(item.SSLStatus),
                item.Favorite,
                websiteGroupId ?? 0,
                groupName,
                item.ChildSites ?? [],
                Text(item.ParentSite)))
            .ToArray();

        var total = (int)Math.Clamp(data.Total, 0, int.MaxValue);
        return (rows, total, total > rows.Length);
    }

    private async Task<IReadOnlyList<WebsiteGroupInfo>> LoadGroupsAsync(
        IPanelClient client,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = await client
            .PostAsync<List<WebsiteGroupDto>>(
                WebsiteRoutes.GroupsSearch,
                new WebsiteGroupSearchRequest("website"),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            warnings.Add($"无法读取网站分组（{result.Error.Message}），已按「{UngroupedName}」展示。");
            return [];
        }

        var groups = (result.Data ?? [])
            .Where(group => string.IsNullOrWhiteSpace(group.Type)
                || string.Equals(group.Type, "website", StringComparison.OrdinalIgnoreCase))
            .Select(group => new WebsiteGroupInfo(
                group.Id,
                group.IsDefault ? DefaultGroupName : Text(group.Name) ?? UngroupedName,
                group.IsDefault))
            .Where(group => group.Id > 0)
            .OrderByDescending(group => group.IsDefault)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return groups;
    }

    private static string ResolveGroupName(IReadOnlyList<WebsiteGroupInfo> groups, long groupId)
    {
        if (groupId <= 0)
        {
            return UngroupedName;
        }

        return groups.FirstOrDefault(group => group.Id == groupId)?.Name ?? $"分组 #{groupId}";
    }

    private static WebsiteSsl? MapSsl(WebsiteSslDto? dto)
    {
        if (dto is null || dto.Id <= 0)
        {
            return null;
        }

        return new WebsiteSsl(
            dto.Id,
            Text(dto.PrimaryDomain) ?? $"#{dto.Id}",
            Text(dto.Domains),
            Text(dto.Type),
            Text(dto.Provider),
            Text(dto.Organization),
            Text(dto.Status) ?? "unknown",
            Text(dto.Message),
            Text(dto.ExpireDate),
            Text(dto.StartDate),
            dto.AutoRenew,
            Text(dto.Description),
            dto.IsIP,
            Text(dto.KeyType),
            Text(dto.CertPath),
            Text(dto.PrivateKeyPath),
            dto.PushDir,
            Text(dto.Dir));
    }

    private async Task<NodeRecord> ResolveNodeAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        if (nodeId is not { } id || id == Guid.Empty)
        {
            throw new WebsiteException("请选择一个节点。", PanelErrorKind.Validation);
        }

        var record = await _nodes.FindAsync(new NodeId(id), cancellationToken).ConfigureAwait(false)
            ?? throw new NodeNotFoundException(new NodeId(id));

        if (!record.IsEnabled)
        {
            throw new WebsiteException($"节点 {record.Name} 已禁用，请先在节点页启用。", PanelErrorKind.Unsupported);
        }

        return record;
    }

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    private static T? EnsureSuccess<T>(PanelCallResult<T> result)
    {
        if (result.IsSuccess)
        {
            return result.Data;
        }

        var error = result.Error ?? new PanelError(PanelErrorKind.Protocol, "1Panel 未返回错误信息。");
        if (error.Kind == PanelErrorKind.PanelServerError
            && error.Message.Contains("record not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebsiteException("网站不存在或已被删除。", PanelErrorKind.NotFound);
        }

        throw new WebsiteException(error);
    }

    private static IReadOnlyList<string> ReadDomains(JsonElement? element)
    {
        if (element is not { } value)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var domains = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                {
                    domains.Add(text.Trim());
                }
                else if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("domain", out var domain)
                    && domain.ValueKind == JsonValueKind.String
                    && domain.GetString() is { Length: > 0 } objectDomain)
                {
                    domains.Add(objectDomain.Trim());
                }
            }

            return domains;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } single)
        {
            return [single.Trim()];
        }

        return [];
    }

    private static string? ParsePhpVersion(string? name, string? image)
    {
        if (!string.IsNullOrWhiteSpace(image))
        {
            var separator = image.LastIndexOf(':');
            if (separator >= 0 && separator + 1 < image.Length)
            {
                return image[(separator + 1)..].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("PHP-", StringComparison.OrdinalIgnoreCase))
        {
            return name["PHP-".Length..].Trim();
        }

        return null;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
