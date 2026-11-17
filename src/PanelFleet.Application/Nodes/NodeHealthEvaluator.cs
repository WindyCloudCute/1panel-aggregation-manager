using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Nodes;

/// <summary>把 1Panel 调用错误映射为节点健康状态。</summary>
public static class NodeHealthEvaluator
{
    public static (NodeHealth Status, string? Message) Evaluate(PanelError? error)
    {
        if (error is null)
        {
            return (NodeHealth.Online, null);
        }

        return error.Kind switch
        {
            PanelErrorKind.Unauthorized or PanelErrorKind.Forbidden =>
                (NodeHealth.Offline, "鉴权失败：API Key、时间同步或 IP 白名单。"),
            PanelErrorKind.Network or PanelErrorKind.Tls or PanelErrorKind.Timeout =>
                (NodeHealth.Offline, error.Message),
            PanelErrorKind.NotFound or PanelErrorKind.Unsupported or PanelErrorKind.Protocol
                or PanelErrorKind.PanelServerError => (NodeHealth.Degraded, error.Message),
            _ => (NodeHealth.Unknown, error.Message)
        };
    }
}
