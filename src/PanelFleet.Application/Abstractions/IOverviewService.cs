using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

/// <summary>聚合总览查询与刷新用例。</summary>
public interface IOverviewService
{
    /// <summary>读取当前快照：数据库节点信息 + 缓存动态数据（不触发采集）。</summary>
    Task<FleetOverview> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>整轮刷新所有启用节点；经队列由后台宿主调用，也可在测试/脚本中直接调用。</summary>
    Task<FleetOverview> RefreshAllAsync(CancellationToken cancellationToken);

    /// <summary>单节点刷新（直通、不排队）；force=true 时绕过熔断。</summary>
    Task<NodeOverviewSnapshot> RefreshNodeAsync(NodeId nodeId, bool force, CancellationToken cancellationToken);
}
