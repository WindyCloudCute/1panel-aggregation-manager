using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Serialization;

/// <summary>全项目统一的 JSON 选项（宽松解析，大小写不敏感）。</summary>
internal static class PanelJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
