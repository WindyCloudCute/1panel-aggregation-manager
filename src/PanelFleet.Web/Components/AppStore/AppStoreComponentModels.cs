using System.Text.Json.Nodes;
using PanelFleet.Application.AppStore;

namespace PanelFleet.Web.Components.AppStore;

/// <summary>「可升级」聚合行：某个节点上待升级的已安装应用与最新版本。</summary>
public sealed record AppUpgradeCandidate(
    Guid NodeId,
    string NodeName,
    InstalledAppItem App,
    AppUpdateVersion? Latest,
    string? Error)
{
    /// <summary>勾选标识（节点 + 安装 ID，节点内唯一）。</summary>
    public string Key => $"{NodeId:N}:{App.Id}";

    /// <summary>只有查到可用版本时才允许勾选/升级。</summary>
    public bool CanSelect => Latest is not null;

    public string CurrentVersion => string.IsNullOrWhiteSpace(App.Version) ? "—" : App.Version;

    public string LatestVersion => Latest?.Version ?? "—";
}

/// <summary>安装表单提交数据（由 <c>InstallAppForm</c> 生成，交给页面转成内核写操作参数）。</summary>
public sealed record AppInstallFormResult(
    string Name,
    string Version,
    long AppDetailId,
    string AppKey,
    string? AppName,
    JsonObject Parameters,
    double CpuQuota,
    long MemoryLimit,
    string MemoryUnit,
    string? ContainerName,
    bool AllowPort,
    string? SpecifyIp,
    string RestartPolicy,
    bool PullImage,
    bool EditCompose,
    string? DockerCompose);

/// <summary>已安装应用配置修改表单提交数据。</summary>
public sealed record AppParamsEditResult(
    long InstallId,
    string Name,
    JsonObject Parameters,
    bool Advanced,
    double? CpuQuota,
    long? MemoryLimit,
    string? MemoryUnit,
    string? ContainerName,
    bool? AllowPort,
    string? SpecifyIp,
    string? RestartPolicy);
