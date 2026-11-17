namespace PanelFleet.Application.Websites;

/// <summary>P7 网站管理模块配置（当前仅作为模块已注册的幂等标记）。</summary>
public sealed class WebsiteOptions
{
    /// <summary>单分组最多拉取网站数（服务端合并前上限）。</summary>
    public int PerGroupLimit { get; set; } = 200;
}
