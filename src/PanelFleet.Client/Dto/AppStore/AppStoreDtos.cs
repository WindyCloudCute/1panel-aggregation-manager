using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.AppStore;

// P5 应用商店 DTO：全部宽松解析（字段缺失/类型变化不抛异常），只映射 UI 与写操作需要的字段。
// 日期等易变字段统一用 string，由应用层按需解析，避免 1Panel 版本差异导致反序列化失败。

/// <summary>应用商店搜索项（<c>POST api/v2/apps/search</c>）。</summary>
public sealed class AppStoreItemDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Key { get; set; }

    public string? Description { get; set; }

    public string? Type { get; set; }

    public string? Status { get; set; }

    public bool Installed { get; set; }

    public int Limit { get; set; }

    public int Recommend { get; set; }

    public bool GpuSupport { get; set; }

    public bool BatchInstallSupport { get; set; }

    public List<string>? Tags { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>应用信息（<c>GET api/v2/apps/{key}</c>），含版本列表。</summary>
public sealed class AppInfoDto
{
    public long Id { get; set; }

    public string? Key { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public string? Status { get; set; }

    public string? Icon { get; set; }

    public string? ShortDescZh { get; set; }

    public string? ShortDescEn { get; set; }

    public string? Description { get; set; }

    public string? Website { get; set; }

    public string? Github { get; set; }

    public string? Document { get; set; }

    public int Recommend { get; set; }

    public int Limit { get; set; }

    public bool Installed { get; set; }

    public bool HostMode { get; set; }

    public bool GpuSupport { get; set; }

    public int MemoryRequired { get; set; }

    public string? Architectures { get; set; }

    public List<string>? Versions { get; set; }

    public List<AppTagDto>? Tags { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>应用商店标签。</summary>
public sealed class AppTagDto
{
    public long Id { get; set; }

    public string? Key { get; set; }

    public string? Name { get; set; }

    public int Sort { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>应用版本详情（<c>GET api/v2/apps/detail/{appId}/{version}/app</c>）。</summary>
public sealed class AppDetailDto
{
    public long Id { get; set; }

    public long AppId { get; set; }

    public string? Version { get; set; }

    public string? DockerCompose { get; set; }

    public string? Image { get; set; }

    public string? Architectures { get; set; }

    public bool Enable { get; set; }

    public bool HostMode { get; set; }

    public bool GpuSupport { get; set; }

    public int MemoryRequired { get; set; }

    public AppParamsDto? Params { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>应用安装参数表单（<c>params.formFields</c>）。</summary>
public sealed class AppParamsDto
{
    public List<AppFormFieldDto>? FormFields { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>安装参数表单字段（端口、密码、下拉选项等）。</summary>
public sealed class AppFormFieldDto
{
    public string? Type { get; set; }

    public string? LabelZh { get; set; }

    public string? LabelEn { get; set; }

    public string? EnvKey { get; set; }

    public string? Key { get; set; }

    public string? Rule { get; set; }

    public bool Required { get; set; }

    public bool Multiple { get; set; }

    public bool Random { get; set; }

    public bool Disabled { get; set; }

    public bool Edit { get; set; }

    public JsonElement? Default { get; set; }

    public List<AppFormValueDto>? Values { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>下拉/多选参数的候选项。</summary>
public sealed class AppFormValueDto
{
    public string? Label { get; set; }

    public string? Value { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>已安装应用列表项（<c>POST api/v2/apps/installed/search</c>）。</summary>
public sealed class InstalledAppItemDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? AppKey { get; set; }

    public string? AppType { get; set; }

    public string? AppName { get; set; }

    public string? Version { get; set; }

    public string? Status { get; set; }

    public string? Message { get; set; }

    public int? HttpPort { get; set; }

    public int? HttpsPort { get; set; }

    public string? Path { get; set; }

    public string? Container { get; set; }

    public string? ServiceName { get; set; }

    public string? WebUI { get; set; }

    public string? CreatedAt { get; set; }

    public bool CanUpdate { get; set; }

    public bool IsEdit { get; set; }

    public bool LinkDB { get; set; }

    public bool Favorite { get; set; }

    public Dictionary<string, JsonElement>? Env { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>已安装应用详情（<c>GET api/v2/apps/installed/info/{id}</c>）。注意 <c>HttpPort</c> 首字母大写。</summary>
public sealed class InstalledAppInfoDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Status { get; set; }

    public string? Message { get; set; }

    public int? HttpPort { get; set; }

    public string? Container { get; set; }

    public string? ComposePath { get; set; }

    public string? AppKey { get; set; }

    public List<int>? AppPorts { get; set; }

    public Dictionary<string, JsonElement>? Env { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>已安装应用参数（<c>GET api/v2/apps/installed/params/{id}</c>）。</summary>
public sealed class InstalledAppConfigDto
{
    public List<InstalledParamDto>? Params { get; set; }

    public string? RawCompose { get; set; }

    public bool Advanced { get; set; }

    public double CpuQuota { get; set; }

    public long MemoryLimit { get; set; }

    public string? MemoryUnit { get; set; }

    public string? ContainerName { get; set; }

    public bool AllowPort { get; set; }

    public bool EditCompose { get; set; }

    public bool HostMode { get; set; }

    public bool PullImage { get; set; }

    public bool GpuConfig { get; set; }

    public string? DockerCompose { get; set; }

    public string? WebUI { get; set; }

    public string? Type { get; set; }

    public string? SpecifyIP { get; set; }

    public string? RestartPolicy { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>已安装应用的单个参数项（值可能是字符串/数字/布尔，统一用 JsonElement）。</summary>
public sealed class InstalledParamDto
{
    public JsonElement? Value { get; set; }

    public bool? Edit { get; set; }

    public string? Key { get; set; }

    public string? Rule { get; set; }

    public string? LabelZh { get; set; }

    public string? LabelEn { get; set; }

    public string? Type { get; set; }

    public string? ShowValue { get; set; }

    public bool? Required { get; set; }

    public bool? Multiple { get; set; }

    public List<AppFormValueDto>? Values { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>应用可升级版本（<c>POST api/v2/apps/installed/update/versions</c>）。</summary>
public sealed class AppUpdateVersionDto
{
    public string? Version { get; set; }

    public long DetailId { get; set; }

    public string? DockerCompose { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>卸载前关联资源项（<c>GET api/v2/apps/installed/delete/check/{id}</c>）。</summary>
public sealed class AppInstallResourceDto
{
    public string? Type { get; set; }

    public string? Name { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>1Panel 异步任务（<c>POST api/v2/logs/tasks/search</c>）。</summary>
public sealed class PanelTaskDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public string? Operate { get; set; }

    public string? Status { get; set; }

    public string? ErrorMsg { get; set; }

    public string? CurrentStep { get; set; }

    public string? LogFile { get; set; }

    public string? CreatedAt { get; set; }

    public string? EndAt { get; set; }

    public long ResourceID { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>应用商店同步状态（<c>GET api/v2/apps/checkupdate</c>）。</summary>
public sealed class AppUpdateCheckDto
{
    public bool CanUpdate { get; set; }

    public bool IsSyncing { get; set; }

    public long AppStoreLastModified { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
