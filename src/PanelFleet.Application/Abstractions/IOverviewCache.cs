using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

/// <summary>进程内总览快照缓存（单例，线程安全）。</summary>
public interface IOverviewCache
{
    OverviewCacheSnapshot GetSnapshot();

    /// <summary>
    /// 写入单节点动态数据。仅当 <c>data.AttemptedAt</c> 晚于已存值时生效（CAS），
    /// 返回是否被采纳；用于整轮刷新与单节点重试并行时防止旧结果覆盖新结果。
    /// </summary>
    bool ApplyNode(NodeId nodeId, NodeOverviewData data);

    /// <summary>清理已删除节点的缓存。</summary>
    void Prune(IReadOnlySet<NodeId> validNodeIds);

    void SetRefreshing(bool refreshing);

    void MarkRunCompleted(TimeSpan elapsed);
}
