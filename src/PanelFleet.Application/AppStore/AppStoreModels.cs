using PanelFleet.Client;

namespace PanelFleet.Application.AppStore;

/// <summary>应用商店搜索条件。分页与过滤语义对齐 1Panel <c>POST apps/search</c>。</summary>
public sealed record AppStoreQuery(
    string? Name = null,
    IReadOnlyList<string>? Tags = null,
    string? Type = null,
    string? Resource = null,
    bool ShowCurrentArch = false,
    int Page = 1,
    int PageSize = 12);

/// <summary>应用商店搜索结果项。</summary>
public sealed record AppStoreItem(
    long Id,
    string Name,
    string Key,
    string Description,
    string Type,
    string Status,
    bool Installed,
    int Limit,
    int Recommend,
    bool GpuSupport,
    bool BatchInstallSupport,
    IReadOnlyList<string> Tags);

/// <summary>应用商店搜索结果页。</summary>
public sealed record AppStorePage(IReadOnlyList<AppStoreItem> Items, int Total, int Page, int PageSize);

/// <summary>应用商店标签。</summary>
public sealed record AppStoreTag(long Id, string Key, string Name);

/// <summary>安装参数下拉项。</summary>
public sealed record AppFormOption(string Label, string Value);

/// <summary>安装参数表单字段。</summary>
public sealed record AppFormField(
    string EnvKey,
    string LabelZh,
    string LabelEn,
    string Type,
    string? DefaultValue,
    bool Required,
    bool Multiple,
    bool Random,
    string? Rule,
    IReadOnlyList<AppFormOption> Options)
{
    /// <summary>按 1Panel 表单约定：<c>password</c>/<c>select</c>/<c>number</c>/<c>text</c>/<c>switch</c> 等。</summary>
    public bool IsSensitive => string.Equals(Type, "password", StringComparison.OrdinalIgnoreCase);
}

/// <summary>应用详情（应用信息 + 指定版本的安装参数表单）。</summary>
public sealed record AppStoreDetail(
    long AppId,
    long DetailId,
    string Key,
    string Name,
    string Version,
    string Type,
    string? Icon,
    string? ShortDescZh,
    string? ShortDescEn,
    string? Website,
    string? Github,
    string? Document,
    bool Installed,
    IReadOnlyList<string> Versions,
    IReadOnlyList<AppFormField> FormFields,
    bool HostMode,
    bool RequiresMemory,
    bool GpuSupport,
    string? DockerCompose);

/// <summary>已安装应用列表项。</summary>
public sealed record InstalledAppItem(
    long Id,
    string Name,
    string AppKey,
    string? AppType,
    string? AppName,
    string Version,
    string Status,
    string Message,
    int? HttpPort,
    int? HttpsPort,
    string? Path,
    string? Container,
    string? ServiceName,
    string? WebUI,
    bool CanUpdate,
    bool IsEdit,
    bool LinkDB,
    bool Favorite,
    DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, string> Env);

/// <summary>已安装应用列表页。</summary>
public sealed record InstalledAppPage(IReadOnlyList<InstalledAppItem> Items, int Total, int Page, int PageSize);

/// <summary>已安装应用详情。</summary>
public sealed record InstalledAppDetail(
    long Id,
    string Name,
    string Version,
    string Status,
    string Message,
    int? HttpPort,
    string? Container,
    string? ComposePath,
    string? AppKey,
    IReadOnlyList<int> AppPorts,
    IReadOnlyDictionary<string, string> Env);

/// <summary>已安装应用参数项。</summary>
public sealed record InstalledParam(
    string Key,
    string LabelZh,
    string LabelEn,
    string Type,
    string? Value,
    bool Edit,
    bool Required,
    bool Multiple,
    string? Rule,
    IReadOnlyList<AppFormOption> Options)
{
    /// <summary>密码等敏感值：界面默认隐藏，审计只记录键名。</summary>
    public bool IsSensitive => string.Equals(Type, "password", StringComparison.OrdinalIgnoreCase);
}

/// <summary>已安装应用配置（参数 + 容器高级配置）。</summary>
public sealed record InstalledAppConfig(
    IReadOnlyList<InstalledParam> Params,
    string? RawCompose,
    bool Advanced,
    double CpuQuota,
    long MemoryLimit,
    string? MemoryUnit,
    string? ContainerName,
    bool AllowPort,
    bool EditCompose,
    bool HostMode,
    bool PullImage,
    bool GpuConfig,
    string? DockerCompose,
    string? WebUI,
    string? Type,
    string? SpecifyIP,
    string? RestartPolicy);

/// <summary>应用可用升级版本。</summary>
public sealed record AppUpdateVersion(string Version, long DetailId, string? DockerCompose);

/// <summary>卸载前检查到的关联资源（数据库/网站等）。</summary>
public sealed record AppInstallResource(string Type, string Name);

/// <summary>1Panel 异步任务状态（用于展示安装/升级/卸载任务进度）。</summary>
public sealed record AppTaskStatus(
    string TaskId,
    string Name,
    string Type,
    string Operate,
    string Status,
    string? ErrorMessage,
    string? CurrentStep,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? FinishedAt)
{
    /// <summary>任务是否仍在执行（1Panel 状态：Executing/Waiting）。</summary>
    public bool IsExecuting =>
        string.Equals(Status, "Executing", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "Waiting", StringComparison.OrdinalIgnoreCase);

    /// <summary>任务是否成功结束。</summary>
    public bool IsSucceeded => string.Equals(Status, "Success", StringComparison.OrdinalIgnoreCase);

    /// <summary>任务是否失败结束。</summary>
    public bool IsFailed =>
        string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "Error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>应用商店同步状态。</summary>
public sealed record AppStoreUpdateCheck(bool CanUpdate, bool IsSyncing);
