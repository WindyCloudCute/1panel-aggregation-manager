using PanelFleet.Domain.Model;

namespace PanelFleet.Application.AppStore;

/// <summary>应用商店只读查询失败（节点不存在之外的 1Panel 错误）。Web 层按 <see cref="Error"/> 映射状态码。</summary>
public sealed class AppStoreException : Exception
{
    public AppStoreException(PanelError error)
        : base(error.Message) => Error = error;

    public AppStoreException(string message, PanelErrorKind kind = PanelErrorKind.Protocol)
        : base(message) => Error = new PanelError(kind, message);

    public PanelError Error { get; }
}
