using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Containers;

// P6 容器 DTO：全部宽松解析（字段缺失/类型变化不抛异常），只映射展示与操作需要的字段。
// 与 Dto/Resources/ResourceDtos.cs 中的 P4 只读容器 DTO 相互独立，避免影响既有资源清单。

/// <summary>容器分页列表项（POST api/v2/containers/search）。</summary>
public sealed class ContainerItemDto
{
    public string? ContainerID { get; set; }

    public string? Name { get; set; }

    public string? ImageID { get; set; }

    public string? ImageName { get; set; }

    public string? CreateTime { get; set; }

    public string? State { get; set; }

    public string? RunTime { get; set; }

    public List<string>? Network { get; set; }

    public List<string>? Ports { get; set; }

    public bool IsFromApp { get; set; }

    public bool IsFromCompose { get; set; }

    public string? AppName { get; set; }

    public string? AppInstallName { get; set; }

    public List<string>? Websites { get; set; }

    public bool IsPinned { get; set; }

    public string? Description { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>容器状态汇总（GET api/v2/containers/status）。</summary>
public sealed class ContainerStatusDto
{
    public int Created { get; set; }

    public int Running { get; set; }

    public int Paused { get; set; }

    public int Restarting { get; set; }

    public int Removing { get; set; }

    public int Exited { get; set; }

    public int Dead { get; set; }

    public int ContainerCount { get; set; }

    public int ComposeCount { get; set; }

    public int ComposeTemplateCount { get; set; }

    public int ImageCount { get; set; }

    public int NetworkCount { get; set; }

    public int VolumeCount { get; set; }

    public int RepoCount { get; set; }
}

/// <summary>单容器实时统计（GET api/v2/containers/stats/{id}）。内存单位为 MiB。</summary>
public sealed class ContainerRuntimeStatsDto
{
    public double CpuPercent { get; set; }

    public double Memory { get; set; }

    public double Cache { get; set; }

    public double IoRead { get; set; }

    public double IoWrite { get; set; }

    public double NetworkRX { get; set; }

    public double NetworkTX { get; set; }

    public DateTimeOffset ShotTime { get; set; }
}

/// <summary>批量容器统计（GET api/v2/containers/list/stats），用于聚合列表展示 CPU/内存。</summary>
public sealed class ContainerListStatsDto
{
    public string? ContainerID { get; set; }

    public double CpuTotalUsage { get; set; }

    public double SystemUsage { get; set; }

    public double CpuPercent { get; set; }

    public double PercpuUsage { get; set; }

    public double MemoryCache { get; set; }

    public double MemoryUsage { get; set; }

    public double MemoryLimit { get; set; }

    public double MemoryPercent { get; set; }
}

/// <summary>容器磁盘占用（POST api/v2/containers/item/stats）。</summary>
public sealed class ContainerItemStatsDto
{
    public long? SizeRw { get; set; }

    public long? SizeRootFs { get; set; }
}

/// <summary>Compose 项目（POST api/v2/containers/compose/search）。</summary>
public sealed class ComposeItemDto
{
    public string? Name { get; set; }

    public string? CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public int ContainerCount { get; set; }

    public int RunningCount { get; set; }

    public string? ConfigFile { get; set; }

    public string? Workdir { get; set; }

    public bool ComposeFileExists { get; set; }

    public bool IsPinned { get; set; }

    public string? Path { get; set; }

    public List<ComposeContainerDto>? Containers { get; set; }

    // 注意：1Panel 的 ComposeInfo 还包含 env 字段，其中可能有明文数据库密码等敏感值。
    // 本项目有意不映射 env（也不使用 JsonExtensionData），确保 Compose 环境变量不会进入 DTO、
    // 更不会返回给浏览器或写入日志/审计。
}

/// <summary>Compose 项目下的容器。</summary>
public sealed class ComposeContainerDto
{
    public string? ContainerID { get; set; }

    public string? Name { get; set; }

    public string? CreateTime { get; set; }

    public string? State { get; set; }

    public List<string>? Ports { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>镜像信息（GET api/v2/containers/image/all、POST api/v2/containers/image/search）。</summary>
public sealed class ContainerImageDto
{
    public string? ID { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public bool IsUsed { get; set; }

    public List<string>? Tags { get; set; }

    public long Size { get; set; }

    public bool IsPinned { get; set; }

    public string? Description { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>容器搜索请求体（PageContainer）。state/orderBy/order 为 1Panel 必填参数。</summary>
public sealed record ContainerSearchRequest(
    int Page,
    int PageSize,
    string? Name,
    string State,
    string Filters,
    string OrderBy,
    string Order,
    bool ExcludeAppStore = false);

/// <summary>容器批量操作请求体（ContainerOperation）：operation ∈ up/start/stop/restart/kill/pause/unpause/remove。</summary>
public sealed record ContainerOperateRequest(string TaskID, IReadOnlyList<string> Names, string Operation);

/// <summary>容器重命名请求体（ContainerRename）。</summary>
public sealed record ContainerRenameRequest(string Name, string NewName);

/// <summary>容器/Compose 详情请求体（InspectReq）：type ∈ container/compose/image/network/volume。</summary>
public sealed record ContainerInspectRequest(string ID, string Type, string? Detail = null);

/// <summary>Compose 操作请求体（ComposeOperation）：operation ∈ up/start/restart/stop/down/delete/rebuild。</summary>
public sealed record ComposeOperateRequest(
    string Name,
    string Path,
    string Operation,
    bool WithFile = false,
    bool Force = false);

/// <summary>Compose 搜索请求体（SearchWithPage）。</summary>
public sealed record ComposeSearchRequest(int Page, int PageSize, string? Info, bool ExcludeAppStore = false);

/// <summary>
/// Compose 创建请求体（ComposeCreate）。from=edit 时按 file 内容创建；from=path 时复用 path 目录下的配置。
/// <para>taskID 为调用方生成的 UUID，仅用于关联 1Panel 后台任务日志；创建本身是异步任务，HTTP 成功只代表已受理。</para>
/// </summary>
public sealed record ComposeCreateRequest(
    string TaskID,
    string? Name,
    string? DirName,
    string From,
    string? File,
    string? Path,
    string? Env,
    bool ForcePull = false);

/// <summary>
/// Compose 更新请求体（ComposeUpdate）。path 为项目目录，detailPath 为 docker-compose.yml 实际路径；
/// 更新同样是异步任务，1Panel 在失败时会尝试回滚旧文件。
/// </summary>
public sealed record ComposeUpdateRequest(
    string TaskID,
    string Name,
    string Path,
    string? DetailPath,
    string Content,
    string? Env,
    bool ForcePull = false);

/// <summary>
/// 只读读取单个文件（FileContent，1Panel v2.3.2 agent 路由）：仅用于 Compose 编辑弹窗预填 YAML。
/// <para>isDetail=true 时只返回元数据；读取内容时传 false。</para>
/// </summary>
public sealed record FileContentRequest(string Path, bool IsDetail = false);

/// <summary>镜像搜索请求体（PageImage）：orderBy/order 为必填参数。</summary>
public sealed record ImageSearchRequest(int Page, int PageSize, string? Name, string OrderBy, string Order);
