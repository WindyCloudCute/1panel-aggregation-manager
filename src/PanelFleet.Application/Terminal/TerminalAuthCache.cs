using System.Collections.Concurrent;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Terminal;

/// <summary>
/// 节点 → 实际生效的 1Panel 鉴权方式缓存（singleton）。
/// Auto 模式下第一次成功连接后记住结果，避免每次终端连接都做 HMAC→MD5 双握手。
/// </summary>
public sealed class TerminalAuthCache
{
    private readonly ConcurrentDictionary<NodeId, AuthMode> _modes = new();

    public AuthMode? TryGet(NodeId nodeId) =>
        _modes.TryGetValue(nodeId, out var mode) ? mode : null;

    public void Remember(NodeId nodeId, AuthMode mode)
    {
        if (mode is AuthMode.HmacSha256 or AuthMode.Md5)
        {
            _modes[nodeId] = mode;
        }
    }

    public void Forget(NodeId nodeId) => _modes.TryRemove(nodeId, out _);
}
