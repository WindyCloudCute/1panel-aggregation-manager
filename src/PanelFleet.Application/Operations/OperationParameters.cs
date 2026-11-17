using System.Text.Json.Nodes;

namespace PanelFleet.Application.Operations;

/// <summary>写操作参数（JSON 对象）的只读访问器，供各业务处理器解析参数。</summary>
public sealed class OperationParameters
{
    private readonly JsonObject _values;

    private OperationParameters(JsonObject values) => _values = values;

    public static OperationParameters From(JsonObject? values) =>
        new(values is null ? new JsonObject() : (JsonObject)values.DeepClone());

    /// <summary>原始 JSON（处理器需要自定义解析时使用）。</summary>
    public JsonObject Raw => _values;

    public IReadOnlyList<string> Keys => _values.Select(pair => pair.Key).ToArray();

    public bool Contains(string name) => _values.ContainsKey(name);

    public string? GetString(string name)
    {
        if (!_values.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToJsonString().Trim('"');
    }

    public string RequireString(string name, string label) =>
        GetString(name) is { Length: > 0 } value ? value : throw OperationValidationException.Missing(label);

    public long? GetInt64(string name)
    {
        if (!_values.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text)
                && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public int? GetInt32(string name) => GetInt64(name) is { } value ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : null;

    public bool GetBoolean(string name, bool fallback = false)
    {
        if (!_values.TryGetPropertyValue(name, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return bool.TryParse(text, out var parsed) ? parsed : fallback;
            }

            if (value.TryGetValue<long>(out var number))
            {
                return number != 0;
            }
        }

        return fallback;
    }

    public JsonObject? GetObject(string name) =>
        _values.TryGetPropertyValue(name, out var node) ? node as JsonObject : null;

    public JsonArray? GetArray(string name) =>
        _values.TryGetPropertyValue(name, out var node) ? node as JsonArray : null;

    public IReadOnlyList<string> GetStringArray(string name)
    {
        if (GetArray(name) is not { } array)
        {
            return [];
        }

        var items = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                items.Add(text);
            }
        }

        return items;
    }

    /// <summary>读取字符串字典（如应用安装的 env 变量），非字符串值按 JSON 文本返回。</summary>
    public IReadOnlyDictionary<string, string> GetStringMap(string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (GetObject(name) is not { } obj)
        {
            return map;
        }

        foreach (var pair in obj)
        {
            var text = pair.Value is JsonValue value && value.TryGetValue<string>(out var stringValue)
                ? stringValue
                : pair.Value?.ToJsonString() ?? string.Empty;
            map[pair.Key] = text;
        }

        return map;
    }
}
