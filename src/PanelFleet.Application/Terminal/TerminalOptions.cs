namespace PanelFleet.Application.Terminal;

/// <summary>P11 终端配置（绑定 <c>Terminal</c> 配置节）。</summary>
public sealed class TerminalOptions
{
    /// <summary>总开关：关闭后页面与 WS 端点均拒绝建立终端。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>空闲断开秒数：无浏览器输入超过该时长即断开（默认 30 分钟）。</summary>
    public int IdleTimeoutSeconds { get; set; } = 1800;

    /// <summary>单会话最长时长（分钟），到点强制断开。</summary>
    public int MaxSessionMinutes { get; set; } = 240;

    /// <summary>浏览器单个输入/输出帧上限（字节）。</summary>
    public int MaxFrameBytes { get; set; } = 65536;

    /// <summary>输入帧频率上限（帧/秒，令牌桶）。</summary>
    public int MaxInputFramesPerSecond { get; set; } = 30;

    /// <summary>输入字节速率上限（字节/秒）。</summary>
    public int MaxInputBytesPerSecond { get; set; } = 131072;

    /// <summary>进程内并发终端会话上限。</summary>
    public int MaxConcurrentSessions { get; set; } = 20;

    /// <summary>额外允许的 Origin（同源始终允许；反代/自定义域名场景使用）。</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>是否允许不带 Origin 的请求（非浏览器客户端）；仍需通过 Admin 鉴权。</summary>
    public bool AllowMissingOrigin { get; set; } = true;

    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(IdleTimeoutSeconds);

    public TimeSpan MaxSessionDuration => TimeSpan.FromMinutes(MaxSessionMinutes);

    public void Validate()
    {
        if (!Enabled)
        {
            // 关闭状态下仅做宽松校验，避免旧配置阻塞启动。
            return;
        }

        EnsureRange(IdleTimeoutSeconds, 30, 86400, nameof(IdleTimeoutSeconds));
        EnsureRange(MaxSessionMinutes, 1, 1440, nameof(MaxSessionMinutes));
        EnsureRange(MaxFrameBytes, 1024, 1048576, nameof(MaxFrameBytes));
        EnsureRange(MaxInputFramesPerSecond, 5, 1000, nameof(MaxInputFramesPerSecond));
        EnsureRange(MaxInputBytesPerSecond, 1024, 10485760, nameof(MaxInputBytesPerSecond));
        EnsureRange(MaxConcurrentSessions, 1, 200, nameof(MaxConcurrentSessions));

        foreach (var origin in AllowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(origin)
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"Terminal:AllowedOrigins 包含非法 Origin「{origin}」，应为 http(s)://host[:port]。", nameof(AllowedOrigins));
            }
        }
    }

    private static void EnsureRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Terminal:{name} 必须在 {min}..{max} 之间。");
        }
    }
}
