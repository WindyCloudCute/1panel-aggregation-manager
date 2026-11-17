using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Logs;

// P10 日志 DTO：全部宽松解析（字段缺失/类型变化不抛异常），只映射展示需要的字段。
// 安全约束：系统日志的 raw 字段（journal 完整 JSON，可能含命令行/环境变量）不进入 DTO。

/// <summary>1Panel 分页信封 <c>{total,items}</c>（日志类查询复用）。</summary>
public sealed class PagedLogItemsDto<T>
{
    public long Total { get; set; }

    public List<T>? Items { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary><c>POST /api/v2/core/logs/operation</c> 的单项。</summary>
public sealed class PanelOperationLogDto
{
    public long Id { get; set; }

    public string? Source { get; set; }

    public string? User { get; set; }

    public string? Ip { get; set; }

    public string? Path { get; set; }

    public string? Method { get; set; }

    public string? Status { get; set; }

    public string? Message { get; set; }

    public string? DetailZH { get; set; }

    /// <summary>耗时（纳秒，1Panel 直接序列化 time.Duration）。</summary>
    public long? Latency { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary><c>POST /api/v2/core/logs/login</c> 的单项。</summary>
public sealed class PanelLoginLogDto
{
    public long Id { get; set; }

    public string? Ip { get; set; }

    public string? User { get; set; }

    public string? Address { get; set; }

    public string? Agent { get; set; }

    public string? Status { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary><c>GET /api/v2/logs/system/status</c> 响应。</summary>
public sealed class SystemLogStatusDto
{
    public string? Source { get; set; }

    public string? Version { get; set; }

    public bool KeywordFilterSupported { get; set; }

    public string? Message { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary><c>POST /api/v2/logs/system/read</c> 响应。</summary>
public sealed class SystemLogPageDto
{
    public string? Source { get; set; }

    public List<SystemLogItemDto>? Items { get; set; }

    public bool HasMore { get; set; }

    public string? NextCursor { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>系统日志单项（不保留 raw 字段，避免把 journal 完整 JSON 带入页面/响应）。</summary>
public sealed class SystemLogItemDto
{
    public string? Time { get; set; }

    public string? Priority { get; set; }

    public string? Service { get; set; }

    public string? Message { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>网站列表查询结果（仅用于把域名解析为网站 id）。</summary>
public sealed class WebsiteLogTargetPageDto
{
    public long Total { get; set; }

    public List<WebsiteLogTargetDto>? Items { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>日志页需要的网站标识字段。</summary>
public sealed class WebsiteLogTargetDto
{
    public long Id { get; set; }

    public string? PrimaryDomain { get; set; }

    public string? Alias { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary><c>POST /api/v2/websites/log/search</c> 响应（content 为一段文本，end 表示是否到文件末尾）。</summary>
public sealed class WebsiteLogDto
{
    public bool Enable { get; set; }

    public string? Content { get; set; }

    public bool End { get; set; }

    public string? Path { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
