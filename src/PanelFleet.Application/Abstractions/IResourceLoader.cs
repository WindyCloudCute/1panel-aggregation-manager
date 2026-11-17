using PanelFleet.Application.Models;
using PanelFleet.Application.Resources;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

/// <summary>单类资源的采集器（每个节点一次调用一个实例）。</summary>
public interface IResourceLoader
{
    ResourceKind Kind { get; }

    Task<NodeResourceResult> LoadAsync(
        IPanelClient client,
        NodeRecord node,
        ResourceOptions options,
        CancellationToken cancellationToken);

    /// <summary>按需加载单条详情；数据库无独立详情接口，返回列表字段组成的详情。</summary>
    Task<ResourceDetail> LoadDetailAsync(
        IPanelClient client,
        NodeRecord node,
        string sourceId,
        CancellationToken cancellationToken);
}
