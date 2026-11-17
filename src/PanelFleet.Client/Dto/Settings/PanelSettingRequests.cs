using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Settings;

/// <summary>通用面板设置更新请求：POST /api/v2/core/settings/update。</summary>
public sealed class PanelSettingUpdateRequest
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

/// <summary>终端设置更新请求：POST /api/v2/core/settings/terminal/update（未赋值字段不发送）。</summary>
public sealed class PanelTerminalUpdateRequest
{
    [JsonPropertyName("showTerminalButton")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShowTerminalButton { get; init; }

    [JsonPropertyName("lineHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LineHeight { get; init; }

    [JsonPropertyName("letterSpacing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LetterSpacing { get; init; }

    [JsonPropertyName("fontSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontSize { get; init; }

    [JsonPropertyName("fontFamily")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontFamily { get; init; }

    [JsonPropertyName("backgroundColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundColor { get; init; }

    [JsonPropertyName("foregroundColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForegroundColor { get; init; }

    [JsonPropertyName("cursorBlink")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CursorBlink { get; init; }

    [JsonPropertyName("cursorStyle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CursorStyle { get; init; }

    [JsonPropertyName("scrollback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scrollback { get; init; }

    [JsonPropertyName("scrollSensitivity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScrollSensitivity { get; init; }
}

/// <summary>当前用户更新请求：POST /api/v2/core/auth/current/update（改用户名/密码）。</summary>
public sealed class PanelCurrentUserUpdateRequest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; init; }

    [JsonPropertyName("oldPassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldPassword { get; init; }
}

/// <summary>API 接口配置更新请求：POST /api/v2/core/auth/api/update。</summary>
public sealed class PanelApiConfigUpdateRequest
{
    [JsonPropertyName("apiInterfaceStatus")]
    public string ApiInterfaceStatus { get; init; } = string.Empty;

    [JsonPropertyName("apiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; init; }

    [JsonPropertyName("ipWhiteList")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IpWhiteList { get; init; }

    [JsonPropertyName("apiTrustedProxies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiTrustedProxies { get; init; }

    [JsonPropertyName("apiKeyValidityTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyValidityTime { get; init; }
}
