using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.AppStore;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.AppStore;

/// <summary>
/// P5 应用商店只读查询（scoped）：应用商店搜索/标签/版本详情、已安装应用列表/详情/参数、
/// 可升级版本、卸载关联资源检查与 1Panel 异步任务状态。所有写操作由 <c>IOperationHandler</c> 执行。
/// </summary>
public sealed class AppStoreService
{
    private const int MaxPageSize = 200;

    private readonly INodeStore _nodes;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly ILogger<AppStoreService> _logger;

    public AppStoreService(
        INodeStore nodes,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        ILogger<AppStoreService> logger)
    {
        _nodes = nodes;
        _protector = protector;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>搜索应用商店应用（分页、按名称/标签/类型过滤）。</summary>
    public Task<AppStorePage> SearchAsync(NodeId nodeId, AppStoreQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 12 : query.PageSize, 1, MaxPageSize);

        return WithClientAsync(
            nodeId,
            async client =>
            {
                var body = new
                {
                    page,
                    pageSize,
                    name = query.Name ?? string.Empty,
                    tags = query.Tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray() ?? [],
                    type = query.Type ?? string.Empty,
                    resource = string.IsNullOrWhiteSpace(query.Resource) ? "all" : query.Resource,
                    showCurrentArch = query.ShowCurrentArch
                };

                var result = await client
                    .PostAsync<PagedItemsDto<AppStoreItemDto>>(AppStoreRoutes.Search, body, cancellationToken)
                    .ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (data, nodeName) =>
            {
                var items = (data?.Items ?? [])
                    .Select(item => new AppStoreItem(
                        item.Id,
                        Text(item.Name) ?? string.Empty,
                        Text(item.Key) ?? string.Empty,
                        Text(item.Description) ?? string.Empty,
                        Text(item.Type) ?? string.Empty,
                        Text(item.Status) ?? string.Empty,
                        item.Installed,
                        item.Limit,
                        item.Recommend,
                        item.GpuSupport,
                        item.BatchInstallSupport,
                        item.Tags ?? []))
                    .ToArray();

                _logger.LogDebug("应用商店搜索完成：节点 {Node} 总数 {Total} 返回 {Count}", nodeName, data?.Total ?? 0, items.Length);

                return new AppStorePage(
                    items,
                    (int)Math.Clamp(data?.Total ?? items.Length, 0, int.MaxValue),
                    page,
                    pageSize);
            },
            cancellationToken);
    }

    /// <summary>应用商店标签列表（用于分类筛选）。</summary>
    public Task<IReadOnlyList<AppStoreTag>> GetTagsAsync(NodeId nodeId, CancellationToken cancellationToken) =>
        WithClientAsync(
            nodeId,
            async client =>
            {
                var result = await client.GetAsync<List<AppTagDto>>(AppStoreRoutes.Tags, cancellationToken).ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (tags, _) => (IReadOnlyList<AppStoreTag>)(tags ?? [])
                .Select(tag => new AppStoreTag(tag.Id, Text(tag.Key) ?? string.Empty, Text(tag.Name) ?? string.Empty))
                .Where(tag => tag.Key.Length > 0)
                .ToArray(),
            cancellationToken);

    /// <summary>按应用 key 与版本获取应用详情（含安装参数表单）。未指定版本时使用商店最新版本。</summary>
    public async Task<AppStoreDetail> GetDetailAsync(
        NodeId nodeId,
        string appKey,
        string? version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            throw new AppStoreException("应用 key 不能为空。", PanelErrorKind.Validation);
        }

        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        var infoResult = await client
            .GetAsync<AppInfoDto>(AppStoreRoutes.App(appKey.Trim()), cancellationToken)
            .ConfigureAwait(false);
        var info = EnsureSuccess(infoResult)
            ?? throw new AppStoreException($"应用 {appKey} 不存在或商店未同步。", PanelErrorKind.NotFound);

        var versions = info.Versions?.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray() ?? [];
        if (versions.Length == 0)
        {
            throw new AppStoreException($"应用 {appKey} 没有可用版本，请先在 1Panel 同步应用商店。", PanelErrorKind.NotFound);
        }

        var selectedVersion = string.IsNullOrWhiteSpace(version) ? versions[0] : version.Trim();
        var detailResult = await client
            .GetAsync<AppDetailDto>(AppStoreRoutes.AppDetail(info.Id, selectedVersion), cancellationToken)
            .ConfigureAwait(false);
        var detail = EnsureSuccess(detailResult)
            ?? throw new AppStoreException($"应用 {appKey} 版本 {selectedVersion} 的详情不存在。", PanelErrorKind.NotFound);

        return new AppStoreDetail(
            info.Id,
            detail.Id,
            Text(info.Key) ?? appKey.Trim(),
            Text(info.Name) ?? info.Key ?? appKey.Trim(),
            Text(detail.Version) ?? selectedVersion,
            Text(info.Type) ?? string.Empty,
            Text(info.Icon),
            Text(info.ShortDescZh),
            Text(info.ShortDescEn),
            Text(info.Website),
            Text(info.Github),
            Text(info.Document),
            info.Installed,
            versions,
            MapFormFields(detail.Params?.FormFields),
            detail.HostMode,
            detail.MemoryRequired > 0,
            detail.GpuSupport,
            Text(detail.DockerCompose));
    }

    /// <summary>已安装应用列表（分页、按名称/标签过滤；<paramref name="updateOnly"/> 时仅返回可升级应用）。</summary>
    public Task<InstalledAppPage> SearchInstalledAsync(
        NodeId nodeId,
        string? name,
        IReadOnlyList<string>? tags,
        bool updateOnly,
        bool checkUpdate,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var currentPage = Math.Max(1, page);
        var currentPageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, MaxPageSize);

        return WithClientAsync(
            nodeId,
            async client =>
            {
                var body = new
                {
                    page = currentPage,
                    pageSize = currentPageSize,
                    name = name ?? string.Empty,
                    tags = tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray() ?? [],
                    update = updateOnly,
                    checkUpdate,
                    sync = false,
                    all = false
                };

                var result = await client
                    .PostAsync<PagedItemsDto<InstalledAppItemDto>>(AppStoreRoutes.InstalledSearch, body, cancellationToken)
                    .ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (data, _) =>
            {
                var items = (data?.Items ?? []).Select(MapInstalledItem).ToArray();
                return new InstalledAppPage(
                    items,
                    (int)Math.Clamp(data?.Total ?? items.Length, 0, int.MaxValue),
                    currentPage,
                    currentPageSize);
            },
            cancellationToken);
    }

    /// <summary>已安装应用详情。</summary>
    public Task<InstalledAppDetail> GetInstalledAsync(
        NodeId nodeId,
        long installId,
        CancellationToken cancellationToken) =>
        WithClientAsync(
            nodeId,
            async client =>
            {
                var result = await client
                    .GetAsync<InstalledAppInfoDto>(AppStoreRoutes.InstalledInfo(installId), cancellationToken)
                    .ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (data, _) => data is null
                ? throw new AppStoreException($"已安装应用 {installId} 不存在。", PanelErrorKind.NotFound)
                : new InstalledAppDetail(
                    data.Id,
                    Text(data.Name) ?? string.Empty,
                    Text(data.Version) ?? string.Empty,
                    Text(data.Status) ?? string.Empty,
                    Text(data.Message) ?? string.Empty,
                    data.HttpPort,
                    Text(data.Container),
                    Text(data.ComposePath),
                    Text(data.AppKey),
                    data.AppPorts ?? [],
                    ReadStringMap(data.Env)),
            cancellationToken);

    /// <summary>已安装应用当前参数（端口、密码等）。</summary>
    public Task<InstalledAppConfig> GetInstalledParamsAsync(
        NodeId nodeId,
        long installId,
        CancellationToken cancellationToken) =>
        WithClientAsync(
            nodeId,
            async client =>
            {
                var result = await client
                    .GetAsync<InstalledAppConfigDto>(AppStoreRoutes.InstalledParams(installId), cancellationToken)
                    .ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (data, _) =>
            {
                var config = data ?? new InstalledAppConfigDto();
                var parameters = (config.Params ?? [])
                    .Select(parameter => new InstalledParam(
                        Text(parameter.Key) ?? string.Empty,
                        Text(parameter.LabelZh) ?? Text(parameter.LabelEn) ?? Text(parameter.Key) ?? string.Empty,
                        Text(parameter.LabelEn) ?? string.Empty,
                        Text(parameter.Type) ?? "text",
                        JsonValueText(parameter.Value),
                        parameter.Edit ?? true,
                        parameter.Required ?? false,
                        parameter.Multiple ?? false,
                        Text(parameter.Rule),
                        (parameter.Values ?? [])
                            .Select(option => new AppFormOption(Text(option.Label) ?? string.Empty, Text(option.Value) ?? string.Empty))
                            .ToArray()))
                    .Where(parameter => parameter.Key.Length > 0)
                    .ToArray();

                return new InstalledAppConfig(
                    parameters,
                    Text(config.RawCompose),
                    config.Advanced,
                    config.CpuQuota,
                    config.MemoryLimit,
                    Text(config.MemoryUnit),
                    Text(config.ContainerName),
                    config.AllowPort,
                    config.EditCompose,
                    config.HostMode,
                    config.PullImage,
                    config.GpuConfig,
                    Text(config.DockerCompose),
                    Text(config.WebUI),
                    Text(config.Type),
                    Text(config.SpecifyIP),
                    Text(config.RestartPolicy));
            },
            cancellationToken);

    /// <summary>查询已安装应用可升级到的版本列表（可能为空）。</summary>
    public Task<IReadOnlyList<AppUpdateVersion>> GetUpdateVersionsAsync(
        NodeId nodeId,
        long installId,
        CancellationToken cancellationToken) =>
        WithClientAsync(
            nodeId,
            async client =>
            {
                var result = await client
                    .PostAsync<List<AppUpdateVersionDto>>(
                        AppStoreRoutes.InstalledUpdateVersions,
                        new { appInstallID = installId },
                        cancellationToken)
                    .ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (versions, _) => (IReadOnlyList<AppUpdateVersion>)(versions ?? [])
                .Where(version => !string.IsNullOrWhiteSpace(version.Version) && version.DetailId > 0)
                .Select(version => new AppUpdateVersion(
                    version.Version!.Trim(),
                    version.DetailId,
                    Text(version.DockerCompose)))
                .OrderBy(version => version.Version, VersionComparer.Instance)
                .ToArray(),
            cancellationToken);

    /// <summary>版本字符串升序比较（数字段按数值比较，兼容 v 前缀）；1Panel 契约保证升序，这里兜底避免上游顺序变化影响「最新版本」判断。</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y) => CompareVersions(x ?? string.Empty, y ?? string.Empty);

        private static int CompareVersions(string left, string right)
        {
            var leftParts = left.Split('.');
            var rightParts = right.Split('.');
            var length = Math.Max(leftParts.Length, rightParts.Length);
            for (var index = 0; index < length; index++)
            {
                var leftPart = TrimPrefix(index < leftParts.Length ? leftParts[index] : "0");
                var rightPart = TrimPrefix(index < rightParts.Length ? rightParts[index] : "0");
                if (long.TryParse(leftPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftNumber)
                    && long.TryParse(rightPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightNumber))
                {
                    if (leftNumber != rightNumber)
                    {
                        return leftNumber.CompareTo(rightNumber);
                    }

                    continue;
                }

                var comparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimPrefix(string value) =>
            value.Length > 0 && (value[0] == 'v' || value[0] == 'V') ? value[1..] : value;
    }

    /// <summary>卸载前检查关联资源（数据库、网站等），为卸载确认弹窗提供警告。</summary>
    public Task<IReadOnlyList<AppInstallResource>> GetDeleteCheckAsync(
        NodeId nodeId,
        long installId,
        CancellationToken cancellationToken) =>
        WithClientAsync(
            nodeId,
            async client =>
            {
                var result = await client
                    .GetAsync<List<AppInstallResourceDto>>(AppStoreRoutes.InstalledDeleteCheck(installId), cancellationToken)
                    .ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (resources, _) => (IReadOnlyList<AppInstallResource>)(resources ?? [])
                .Select(resource => new AppInstallResource(
                    Text(resource.Type) ?? string.Empty,
                    Text(resource.Name) ?? string.Empty))
                .ToArray(),
            cancellationToken);

    /// <summary>查询 1Panel 异步任务状态；任务不存在时返回 <c>null</c>。</summary>
    public Task<AppTaskStatus?> GetTaskStatusAsync(
        NodeId nodeId,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult<AppTaskStatus?>(null);
        }

        return WithClientAsync(
            nodeId,
            async client =>
            {
                var result = await client
                    .PostAsync<PagedItemsDto<PanelTaskDto>>(
                        AppStoreRoutes.TaskSearch,
                        new { page = 1, pageSize = 20, taskID = taskId.Trim() },
                        cancellationToken)
                    .ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (data, _) =>
            {
                var task = (data?.Items ?? [])
                    .FirstOrDefault(item => string.Equals(Text(item.Id), taskId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (task is null)
                {
                    return null;
                }

                return new AppTaskStatus(
                    Text(task.Id) ?? taskId.Trim(),
                    Text(task.Name) ?? string.Empty,
                    Text(task.Type) ?? string.Empty,
                    Text(task.Operate) ?? string.Empty,
                    Text(task.Status) ?? string.Empty,
                    Text(task.ErrorMsg),
                    Text(task.CurrentStep),
                    ParseDate(task.CreatedAt),
                    ParseDate(task.EndAt));
            },
            cancellationToken);
    }

    /// <summary>应用商店是否有可同步更新（只读检查）。</summary>
    public Task<AppStoreUpdateCheck> CheckStoreUpdateAsync(NodeId nodeId, CancellationToken cancellationToken) =>
        WithClientAsync(
            nodeId,
            async client =>
            {
                var result = await client.GetAsync<AppUpdateCheckDto>(AppStoreRoutes.CheckUpdate, cancellationToken).ConfigureAwait(false);
                return EnsureSuccess(result);
            },
            (data, _) => new AppStoreUpdateCheck(data?.CanUpdate ?? false, data?.IsSyncing ?? false),
            cancellationToken);

    private async Task<TResult> WithClientAsync<TData, TResult>(
        NodeId nodeId,
        Func<IPanelClient, Task<TData?>> call,
        Func<TData?, string, TResult> map,
        CancellationToken cancellationToken)
    {
        var record = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory.Create(BuildDescriptor(record));

        try
        {
            var data = await call(client).ConfigureAwait(false);
            return map(data, record.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "应用商店查询异常：节点 {NodeId}", record.Id);
            throw new AppStoreException($"应用商店查询失败：{exception.Message}", PanelErrorKind.Protocol);
        }
    }

    private static TData? EnsureSuccess<TData>(PanelCallResult<TData> result)
    {
        if (!result.IsSuccess)
        {
            throw new AppStoreException(result.Error ?? new PanelError(PanelErrorKind.Protocol, "1Panel 未返回错误信息。"));
        }

        return result.Data;
    }

    private async Task<NodeRecord> ResolveNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        var record = await _nodes.FindAsync(nodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new NodeNotFoundException(nodeId);

        if (!record.IsEnabled)
        {
            throw new AppStoreException($"节点 {record.Name} 已禁用，请先在节点页启用。", PanelErrorKind.Unsupported);
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

    private static InstalledAppItem MapInstalledItem(InstalledAppItemDto item) => new(
        item.Id,
        Text(item.Name) ?? string.Empty,
        Text(item.AppKey) ?? string.Empty,
        Text(item.AppType),
        Text(item.AppName),
        Text(item.Version) ?? string.Empty,
        Text(item.Status) ?? string.Empty,
        Text(item.Message) ?? string.Empty,
        item.HttpPort,
        item.HttpsPort,
        Text(item.Path),
        Text(item.Container),
        Text(item.ServiceName),
        Text(item.WebUI),
        item.CanUpdate,
        item.IsEdit,
        item.LinkDB,
        item.Favorite,
        ParseDate(item.CreatedAt),
        ReadStringMap(item.Env));

    private static IReadOnlyList<AppFormField> MapFormFields(List<AppFormFieldDto>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return [];
        }

        var mapped = new List<AppFormField>(fields.Count);
        var index = 0;
        foreach (var field in fields)
        {
            var envKey = Text(field.EnvKey) ?? Text(field.Key) ?? $"FIELD_{index}";
            mapped.Add(new AppFormField(
                envKey,
                Text(field.LabelZh) ?? Text(field.LabelEn) ?? envKey,
                Text(field.LabelEn) ?? string.Empty,
                Text(field.Type) ?? "text",
                JsonValueText(field.Default),
                field.Required,
                field.Multiple,
                field.Random,
                Text(field.Rule),
                (field.Values ?? [])
                    .Select(option => new AppFormOption(Text(option.Label) ?? string.Empty, Text(option.Value) ?? string.Empty))
                    .ToArray()));
            index++;
        }

        return mapped;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(Dictionary<string, JsonElement>? map)
    {
        if (map is null || map.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
        foreach (var pair in map)
        {
            result[pair.Key] = JsonValueText(pair.Value) ?? string.Empty;
        }

        return result;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>把 1Panel 的 JsonElement 值（字符串/数字/布尔/嵌套）转为展示文本；对象/数组保留原始 JSON。</summary>
    internal static string? JsonValueText(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
