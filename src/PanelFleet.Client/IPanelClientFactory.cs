using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>按节点创建 1Panel 客户端。</summary>
public interface IPanelClientFactory
{
    IPanelClient Create(NodeDescriptor node);
}
