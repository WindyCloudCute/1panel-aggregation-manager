using System.Globalization;

namespace PanelFleet.Application.Cronjobs;

/// <summary>结构化周期类型（与 1Panel 前端 helper.ts 一致）。</summary>
public enum CronjobSpecType
{
    PerMonth = 0,
    PerWeek = 1,
    PerDay = 2,
    PerHour = 3,
    PerNDay = 4,
    PerNHour = 5,
    PerNMinute = 6,
    PerNSecond = 7
}

/// <summary>结构化周期分量。</summary>
public sealed record CronjobSpecParts(
    CronjobSpecType Type,
    int Week = 0,
    int Day = 0,
    int Hour = 0,
    int Minute = 0,
    int Second = 0);

/// <summary>周期表达式构建/校验/中文描述（纯函数，便于单元测试与 UI 复用）。</summary>
public static class CronjobSpec
{
    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>结构化周期 → cron 表达式（或 <c>@every Nm/Ns</c>）。</summary>
    public static string Build(CronjobSpecParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        return parts.Type switch
        {
            CronjobSpecType.PerMonth => $"{ClampMinute(parts.Minute)} {Range(parts.Hour, 0, 23, "小时")} {Range(parts.Day, 1, 31, "日期")} * *",
            CronjobSpecType.PerWeek => $"{ClampMinute(parts.Minute)} {Range(parts.Hour, 0, 23, "小时")} * * {Range(parts.Week, 0, 6, "星期")}",
            CronjobSpecType.PerDay => $"{ClampMinute(parts.Minute)} {Range(parts.Hour, 0, 23, "小时")} * * *",
            CronjobSpecType.PerHour => $"{ClampMinute(parts.Minute)} * * * *",
            CronjobSpecType.PerNDay => $"{ClampMinute(parts.Minute)} {Range(parts.Hour, 0, 23, "小时")} */{Range(parts.Day, 1, 31, "天数")} * *",
            CronjobSpecType.PerNHour => $"{ClampMinute(parts.Minute)} */{Range(parts.Hour, 1, 23, "小时")} * * *",
            CronjobSpecType.PerNMinute => $"@every {Range(parts.Minute, 1, 1440, "分钟")}m",
            CronjobSpecType.PerNSecond => $"@every {Range(parts.Second, 1, 86400, "秒")}s",
            _ => throw new ArgumentOutOfRangeException(nameof(parts), parts.Type, "未知周期类型。")
        };
    }

    /// <summary>是否为受支持的周期表达式（单个 cron 五段式或 @every Nm/Ns）。</summary>
    public static bool IsValid(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var text = spec.Trim();
        if (text.Contains("&&", StringComparison.Ordinal))
        {
            // 多 spec 组合由 1Panel 原生支持，但本模块只创建单 spec；校验时逐段检查。
            return text.Split("&&", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(IsValidSingle);
        }

        return IsValidSingle(text);
    }

    /// <summary>尝试把单 spec 反向解析为结构化分量（失败返回 false，不做猜测）。</summary>
    public static bool TryParse(string? spec, out CronjobSpecParts parts)
    {
        parts = null!;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var text = spec.Trim();
        if (text.StartsWith("@every ", StringComparison.OrdinalIgnoreCase))
        {
            var value = text["@every ".Length..].Trim();
            if (value.EndsWith('m') && int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
            {
                parts = new CronjobSpecParts(CronjobSpecType.PerNMinute, Minute: minutes);
                return true;
            }

            if (value.EndsWith('s') && int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                parts = new CronjobSpecParts(CronjobSpecType.PerNSecond, Second: seconds);
                return true;
            }

            return false;
        }

        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5
            || !TryNumber(fields[0], out var minute)
            || fields[0].Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        var hourField = fields[1];
        if (hourField == "*")
        {
            parts = new CronjobSpecParts(CronjobSpecType.PerHour, Minute: minute);
            return true;
        }

        if (TryStep(hourField, out var hourStep))
        {
            parts = new CronjobSpecParts(CronjobSpecType.PerNHour, Minute: minute, Hour: hourStep);
            return true;
        }

        if (!TryNumber(hourField, out var hour))
        {
            return false;
        }

        if (TryStep(fields[2], out var dayStep))
        {
            parts = new CronjobSpecParts(CronjobSpecType.PerNDay, Minute: minute, Hour: hour, Day: dayStep);
            return true;
        }

        if (fields[2] != "*")
        {
            if (!TryNumber(fields[2], out var day))
            {
                return false;
            }

            parts = new CronjobSpecParts(CronjobSpecType.PerMonth, Minute: minute, Hour: hour, Day: day);
            return true;
        }

        if (fields[4] != "*")
        {
            if (!TryNumber(fields[4], out var week))
            {
                return false;
            }

            parts = new CronjobSpecParts(CronjobSpecType.PerWeek, Week: week, Minute: minute, Hour: hour);
            return true;
        }

        parts = new CronjobSpecParts(CronjobSpecType.PerDay, Minute: minute, Hour: hour);
        return true;
    }

    /// <summary>中文描述；无法解析时返回原始 spec（空则返回「-」）。</summary>
    public static string Describe(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return "-";
        }

        var text = spec.Trim();
        if (text.Contains("&&", StringComparison.Ordinal))
        {
            return text;
        }

        if (!TryParse(text, out var parts))
        {
            return text;
        }

        return parts.Type switch
        {
            CronjobSpecType.PerMonth => $"每月 {parts.Day} 日 {parts.Hour:00}:{parts.Minute:00}",
            CronjobSpecType.PerWeek => $"每{WeekdayName(parts.Week)} {parts.Hour:00}:{parts.Minute:00}",
            CronjobSpecType.PerDay => $"每天 {parts.Hour:00}:{parts.Minute:00}",
            CronjobSpecType.PerHour => $"每小时第 {parts.Minute:00} 分",
            CronjobSpecType.PerNDay => $"每 {parts.Day} 天 {parts.Hour:00}:{parts.Minute:00}",
            CronjobSpecType.PerNHour => $"每 {parts.Hour} 小时第 {parts.Minute:00} 分",
            CronjobSpecType.PerNMinute => $"每 {parts.Minute} 分钟",
            CronjobSpecType.PerNSecond => $"每 {parts.Second} 秒",
            _ => text
        };
    }

    public static string WeekdayName(int week) =>
        week >= 0 && week < WeekdayNames.Length ? WeekdayNames[week] : week.ToString(CultureInfo.InvariantCulture);

    private static bool IsValidSingle(string spec)
    {
        if (spec.StartsWith("@every ", StringComparison.OrdinalIgnoreCase))
        {
            var value = spec["@every ".Length..].Trim();
            if (value.Length < 2)
            {
                return false;
            }

            var unit = value[^1];
            return (unit is 'm' or 's')
                && int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
                && amount > 0;
        }

        var fields = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return false;
        }

        return IsField(fields[0], 0, 59)
            && IsField(fields[1], 0, 23)
            && IsField(fields[2], 1, 31)
            && IsField(fields[3], 1, 12)
            && IsField(fields[4], 0, 6);
    }

    private static bool IsField(string field, int min, int max)
    {
        if (field == "*")
        {
            return true;
        }

        if (field.Contains("*/", StringComparison.Ordinal))
        {
            return int.TryParse(field[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)
                && step >= 1
                && step <= max;
        }

        return int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= min
            && value <= max;
    }

    private static bool TryStep(string field, out int step)
    {
        step = 0;
        return field.StartsWith("*/", StringComparison.Ordinal)
            && int.TryParse(field[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out step)
            && step >= 1;
    }

    private static bool TryNumber(string field, out int value) =>
        int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string ClampMinute(int minute) => Math.Clamp(minute, 0, 59).ToString(CultureInfo.InvariantCulture);

    private static int Range(int value, int min, int max, string label)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(label, value, $"周期参数「{label}」必须在 {min}..{max} 之间。");
        }

        return value;
    }
}
