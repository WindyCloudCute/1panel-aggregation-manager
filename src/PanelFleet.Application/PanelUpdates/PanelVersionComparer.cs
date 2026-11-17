using System.Globalization;

namespace PanelFleet.Application.PanelUpdates;

/// <summary>
/// 1Panel 版本号安全比较：支持 <c>v2.3.2</c>、<c>2.3.2</c>、<c>v2.5.1-beta.1</c>、<c>v2.3.2-lts</c> 等格式。
/// <para>规则：去掉 <c>v</c> 前缀与 <c>+build</c> 元数据；点分数字段逐段比较（缺失段补 0）；
/// <c>-lts</c> 视为正式版；其它预发布后缀（beta/rc…）低于同号正式版。</para>
/// <para>无法解析的版本按"更低"处理，绝不抛出异常（版本信息来自外部面板）。</para>
/// </summary>
public static class PanelVersionComparer
{
    /// <summary>目标版本是否高于当前版本（用于"有更新"判断）；任一侧无法解析时返回 <c>false</c>。</summary>
    public static bool IsUpgrade(string? target, string? current) =>
        TryParse(target, out _) && TryParse(current, out _) && Compare(target, current) > 0;

    /// <summary>比较两个版本号：左小于右返回负数，相等返回 0，左大于右返回正数。</summary>
    public static int Compare(string? left, string? right)
    {
        var hasLeft = TryParse(left, out var leftVersion);
        var hasRight = TryParse(right, out var rightVersion);

        if (!hasLeft && !hasRight)
        {
            return 0;
        }

        if (!hasLeft)
        {
            return -1;
        }

        if (!hasRight)
        {
            return 1;
        }

        var core = leftVersion.CompareCoreTo(rightVersion);
        return core != 0 ? core : leftVersion.ComparePreReleaseTo(rightVersion);
    }

    /// <summary>尝试解析版本号；空串、纯数字缺段、非数字段且无后缀等情况返回 <c>false</c>。</summary>
    public static bool TryParse(string? value, out PanelVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        var buildIndex = text.IndexOf('+', StringComparison.Ordinal);
        if (buildIndex >= 0)
        {
            text = text[..buildIndex];
        }

        string? preRelease = null;
        var dashIndex = text.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            preRelease = text[(dashIndex + 1)..].Trim();
            text = text[..dashIndex];
        }

        if (text.Length == 0)
        {
            return false;
        }

        var parts = text.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            numbers[index] = number;
        }

        // 1Panel 在比较时会给无后缀版本补 "-lts"，因此 -lts 与无后缀视为同一正式版。
        if (string.Equals(preRelease, "lts", StringComparison.OrdinalIgnoreCase))
        {
            preRelease = null;
        }

        version = new PanelVersion(numbers[0], numbers[1], numbers[2], numbers[3], preRelease);
        return true;
    }
}

/// <summary>已解析的面板版本号（四段数字 + 可选预发布后缀）。</summary>
public readonly record struct PanelVersion(int Major, int Minor, int Patch, int Revision, string? PreRelease)
{
    /// <summary>逐段比较数字部分。</summary>
    public int CompareCoreTo(PanelVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        var patch = Patch.CompareTo(other.Patch);
        return patch != 0 ? patch : Revision.CompareTo(other.Revision);
    }

    /// <summary>数字部分相同时比较预发布后缀：正式版高于预发布版。</summary>
    public int ComparePreReleaseTo(PanelVersion other)
    {
        if (PreRelease is null && other.PreRelease is null)
        {
            return 0;
        }

        if (PreRelease is null)
        {
            return 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        return string.Compare(PreRelease, other.PreRelease, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        if (Revision != 0)
        {
            core += "." + Revision.ToString(CultureInfo.InvariantCulture);
        }

        return PreRelease is null ? core : core + "-" + PreRelease;
    }
}
