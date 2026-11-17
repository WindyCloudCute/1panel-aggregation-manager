using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Resources;

/// <summary>把 1Panel 详情响应（JSON）展平为展示字段。</summary>
public static class ResourceFieldMapper
{
    public static IReadOnlyDictionary<string, string?> Flatten(JsonElement element)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return fields;
        }

        foreach (var property in element.EnumerateObject())
        {
            fields[property.Name] = ToDisplay(property.Value);
        }

        return fields;
    }

    public static string? ToDisplay(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(
            ", ",
            value.EnumerateArray()
                .Select(item => ToDisplay(item))
                .Where(item => !string.IsNullOrWhiteSpace(item))),
        JsonValueKind.Object => Truncate(value.GetRawText(), 400),
        _ => value.GetRawText()
    };

    public static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}

/// <summary>五类资源加载器的公共逻辑。</summary>
public abstract class ResourceLoaderBase : IResourceLoader
{
    public abstract ResourceKind Kind { get; }

    public abstract Task<NodeResourceResult> LoadAsync(
        IPanelClient client,
        NodeRecord node,
        ResourceOptions options,
        CancellationToken cancellationToken);

    public virtual Task<ResourceDetail> LoadDetailAsync(
        IPanelClient client,
        NodeRecord node,
        string sourceId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Kind} 不支持详情。");

    protected static IReadOnlyDictionary<string, string?> Fields(params (string Key, string? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);

    protected static async Task<PanelCallResult<PagedItemsDto<TItem>>> SearchAsync<TItem>(
        IPanelClient client,
        string route,
        object body,
        object? fallbackBody,
        CancellationToken cancellationToken)
    {
        var result = await client.PostAsync<PagedItemsDto<TItem>>(route, body, cancellationToken).ConfigureAwait(false);

        // 1Panel v2 参数校验失败是 HTTP 200 + body code=400；不同版本 orderBy 枚举可能变化，回退一次默认排序。
        if (result.Error?.Kind == PanelErrorKind.Validation && fallbackBody is not null)
        {
            result = await client.PostAsync<PagedItemsDto<TItem>>(route, fallbackBody, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    protected static NodeResourceResult Success(
        NodeRecord node,
        ResourceKind kind,
        IReadOnlyList<ResourceRow> rows,
        int total,
        bool truncated,
        IReadOnlyList<string>? warnings,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var now = DateTimeOffset.UtcNow;
        var state = warnings is { Count: > 0 } ? ResourceNodeState.Partial : ResourceNodeState.Success;
        return new NodeResourceResult(
            node.Id,
            node.Name,
            kind,
            state,
            rows,
            total,
            truncated,
            warnings ?? [],
            null,
            now,
            now,
            stopwatch.Elapsed);
    }

    protected static NodeResourceResult Failed(
        NodeRecord node,
        ResourceKind kind,
        PanelError error,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var now = DateTimeOffset.UtcNow;
        return new NodeResourceResult(
            node.Id,
            node.Name,
            kind,
            ResourceNodeState.Failed,
            [],
            0,
            false,
            [],
            error,
            null,
            now,
            stopwatch.Elapsed);
    }

    protected static int ToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    protected static string? Join(JsonElement? element) => element is null ? null : ResourceFieldMapper.ToDisplay(element.Value);

    protected static ResourceRow Row(
        NodeRecord node,
        ResourceKind kind,
        string sourceId,
        string name,
        string? status,
        IReadOnlyDictionary<string, string?> attributes) =>
        new(node.Id, node.Name, kind, sourceId, name, status, attributes);
}
