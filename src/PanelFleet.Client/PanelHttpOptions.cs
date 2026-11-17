namespace PanelFleet.Client;

/// <summary>节点 HTTP 客户端参数。</summary>
public sealed class PanelHttpOptions
{
    /// <summary>单次调用预算（秒）。默认 8 秒。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>TCP 连接超时。默认 5 秒。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>连接池生命周期（用于 DNS 刷新）。默认 5 分钟。</summary>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>每个节点最大并发连接数。默认 4。</summary>
    public int MaxConnectionsPerServer { get; init; } = 4;
}
