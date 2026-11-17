using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Logs;

/// <summary>P10 日志范围：面板（操作/登录）、系统 journal、容器、网站。</summary>
public enum LogScope
{
    Panel = 0,
    System = 1,
    Container = 2,
    Website = 3
}

/// <summary>日志范围解析与中文展示。</summary>
public static class LogScopes
{
    /// <summary>解析 <c>type</c> 查询参数（panel/system/container/website，大小写不敏感）。</summary>
    public static bool TryParse(string? value, out LogScope scope)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "panel":
                scope = LogScope.Panel;
                return true;
            case "system":
                scope = LogScope.System;
                return true;
            case "container":
                scope = LogScope.Container;
                return true;
            case "website":
                scope = LogScope.Website;
                return true;
            default:
                scope = default;
                return false;
        }
    }

    public static string Text(LogScope scope) => scope switch
    {
        LogScope.Panel => "面板日志",
        LogScope.System => "系统日志",
        LogScope.Container => "容器日志",
        LogScope.Website => "网站日志",
        _ => "日志"
    };
}

/// <summary>一次日志查询（只读）。</summary>
public sealed record LogQuery(
    LogScope Scope,
    Guid? NodeId = null,
    string? Container = null,
    string? Website = null,
    string PanelLog = "operation",
    string LogType = "access",
    DateTimeOffset? StartTime = null,
    DateTimeOffset? EndTime = null,
    string? Keyword = null,
    int? Limit = null,
    string? Service = null,
    string? Priority = null,
    string? Cursor = null);

/// <summary>统一日志行（跨范围展示）。</summary>
public sealed record LogEntry(
    string? Time,
    string? Level,
    string? Service,
    string Message,
    string? Detail,
    DateTimeOffset? Timestamp = null);

/// <summary>单节点日志查询状态。</summary>
public enum LogNodeState
{
    Success = 0,
    Failed = 1
}

/// <summary>单节点日志结果（失败按节点隔离，不影响其他节点）。</summary>
public sealed record LogNodeResult(
    Guid NodeId,
    string NodeName,
    LogNodeState State,
    IReadOnlyList<LogEntry> Entries,
    bool Truncated,
    long? Total,
    bool HasMore,
    string? NextCursor,
    PanelError? Error,
    string? ErrorMessage,
    TimeSpan Elapsed)
{
    public bool Success => State == LogNodeState.Success;
}

/// <summary>多节点日志查询结果。</summary>
public sealed record LogQueryResult(
    LogScope Scope,
    IReadOnlyList<LogNodeResult> Nodes,
    int NodesTotal,
    int NodesSucceeded,
    int NodesFailed,
    int TotalEntries,
    int Limit,
    string? Keyword,
    string? Container,
    string? Website,
    string? PanelLog,
    string? LogType,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    DateTimeOffset GeneratedAt);

/// <summary>日志过滤选项（系统日志的可用服务与文件日期）。</summary>
public sealed record LogFilterOptions(
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Files,
    int NodesTotal,
    int NodesFailed,
    DateTimeOffset GeneratedAt);

/// <summary>日志查询参数不合法（Web 层映射为 400）。</summary>
public sealed class LogValidationException : Exception
{
    public LogValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>节点不存在或已禁用（Web 层映射为 404）。</summary>
public sealed class LogNodeNotFoundException : Exception
{
    public LogNodeNotFoundException(Guid nodeId)
        : base($"节点不存在或已禁用（{nodeId}）。")
    {
    }
}
