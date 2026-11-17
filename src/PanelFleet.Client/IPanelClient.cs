using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>单个 1Panel 节点的类型化客户端。</summary>
public interface IPanelClient : IDisposable
{
    NodeDescriptor Node { get; }

    /// <summary>鉴权方式与时钟偏差（探测成功后有效）。</summary>
    PanelAuthState AuthState { get; }

    Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default);

    Task<PanelCallResult<T>> PostAsync<T>(string relativePath, object? body, CancellationToken cancellationToken = default);

    Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default);

    Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(CancellationToken cancellationToken = default);

    Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
        string ioOption = "all",
        string netOption = "all",
        CancellationToken cancellationToken = default);
}
