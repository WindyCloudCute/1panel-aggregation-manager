namespace PanelFleet.Application.Settings;

/// <summary>设置模块选项（由 Host 在 <c>AddSettingsModule</c> 时配置）。</summary>
public sealed class SettingsModuleOptions
{
    /// <summary>
    /// PanelFleet 设置的持久化文件路径；为空时使用 <c>{AppContext.BaseDirectory}/panelfleet-settings.json</c>。
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>读取单个节点面板设置的超时（秒），默认 15。</summary>
    public int PanelRequestTimeoutSeconds { get; set; } = 15;

    /// <summary>校验选项，越界抛 <see cref="ArgumentOutOfRangeException"/>（fail-closed）。</summary>
    public void Validate()
    {
        if (PanelRequestTimeoutSeconds is < 3 or > 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PanelRequestTimeoutSeconds),
                PanelRequestTimeoutSeconds,
                "Settings:PanelRequestTimeoutSeconds 必须在 3..120 之间。");
        }
    }

    /// <summary>解析设置文件绝对路径。</summary>
    public string ResolveFilePath() =>
        string.IsNullOrWhiteSpace(FilePath)
            ? Path.Combine(AppContext.BaseDirectory, "panelfleet-settings.json")
            : Path.GetFullPath(FilePath);
}
