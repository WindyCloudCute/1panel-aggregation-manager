namespace PanelFleet.Application.Containers;

/// <summary>
/// 敏感值脱敏工具：容器/Compose 的环境变量、标签可能包含数据库密码、Token 等。
/// <para>规则：键名包含 PASSWORD/PASSWD/PWD/SECRET/TOKEN/KEY/CREDENTIAL/AUTH 时，值替换为 ***。</para>
/// </summary>
public static class SecretMasker
{
    public const string Mask = "***";

    private static readonly string[] SensitiveTokens =
    [
        "PASSWORD", "PASSWD", "PWD", "SECRET", "TOKEN", "APIKEY", "API_KEY",
        "ACCESS_KEY", "PRIVATE_KEY", "CREDENTIAL", "AUTH"
    ];

    /// <summary>判断环境变量/标签键是否敏感。</summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return SensitiveTokens.Any(token => key.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>对 <c>KEY=VALUE</c> 形式的条目按需脱敏；非 KEY=VALUE 条目原样返回。</summary>
    public static string MaskEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return string.Empty;
        }

        var separator = entry.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return entry;
        }

        var key = entry[..separator];
        return IsSensitiveKey(key) ? key + "=" + Mask : entry;
    }

    /// <summary>按需脱敏单个键值。</summary>
    public static string MaskValue(string? key, string? value) =>
        IsSensitiveKey(key) ? Mask : value ?? string.Empty;
}
