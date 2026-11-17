namespace PanelFleet.Web.Components.Resources;

/// <summary>
/// 资源状态 → 中文文案 / 徽标 CssClass 的统一映射。
/// 页面、树、表格、详情抽屉共用，避免各处重复硬编码状态词。
/// </summary>
public static class ResourceStatusStyle
{
    /// <summary>状态中文文案；null / 空白返回 null（由调用方决定占位符）。</summary>
    public static string? Text(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Normalize(status) switch
        {
            "running" or "run" or "started" or "healthy" or "online" or "active" => "运行中",
            "enable" or "enabled" or "normal" => "正常",
            "installing" => "安装中",
            "creating" => "创建中",
            "starting" => "启动中",
            "stopping" => "停止中",
            "restarting" => "重启中",
            "paused" => "已暂停",
            "pending" => "等待中",
            "stopped" or "stop" or "exited" or "offline" or "inactive" => "已停止",
            "disable" or "disabled" => "停用",
            "error" or "failed" or "unhealthy" or "abnormal" => "异常",
            _ => status.Trim()
        };
    }

    /// <summary>badge 样式类（badge-ok / badge-warn / badge-error / badge-unknown）。</summary>
    public static string CssClass(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "badge-unknown";
        }

        return Normalize(status) switch
        {
            "running" or "run" or "started" or "healthy" or "online" or "active"
                or "enable" or "enabled" or "normal" => "badge-ok",
            "installing" or "creating" or "starting" or "stopping" or "restarting" => "badge-warn",
            "error" or "failed" or "unhealthy" or "abnormal" => "badge-error",
            _ => "badge-unknown"
        };
    }

    private static string Normalize(string status) => status.Trim().ToLowerInvariant();
}
