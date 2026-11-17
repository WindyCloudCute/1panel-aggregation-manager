using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>客户端当前生效的鉴权状态（用于连接测试与巡检回写）。</summary>
public sealed record PanelAuthState(AuthMode EffectiveMode, TimeSpan ClockOffset, bool Resolved);
