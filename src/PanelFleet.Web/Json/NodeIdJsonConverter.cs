using System.Text.Json;
using System.Text.Json.Serialization;
using PanelFleet.Domain.Model;

namespace PanelFleet.Web.Json;

/// <summary>把 NodeId 序列化为字符串，前后端/脚本更易用。</summary>
public sealed class NodeIdJsonConverter : JsonConverter<NodeId>
{
    public override NodeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return Guid.TryParse(value, out var id)
            ? new NodeId(id)
            : throw new JsonException("NodeId 不是合法的 Guid。");
    }

    public override void Write(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString("N"));
}
