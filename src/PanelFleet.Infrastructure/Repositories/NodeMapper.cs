using System.Globalization;
using System.Text.Json;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;
using PanelFleet.Infrastructure.Persistence.Entities;

namespace PanelFleet.Infrastructure.Repositories;

internal static class NodeMapper
{
    private static readonly JsonSerializerOptions TagsJson = new();

    public static NodeRecord ToRecord(NodeEntity entity) => new(
        new NodeId(Guid.Parse(entity.Id)),
        entity.Name,
        new Uri(entity.BaseUrl),
        entity.SecurityEntrance,
        entity.ApiKeyProtected,
        entity.ApiKeyHint,
        (AuthMode)entity.AuthMode,
        entity.DetectedAuthMode is { } detected ? (AuthMode)detected : null,
        entity.ClockOffsetSeconds,
        (TlsPolicy)entity.TlsPolicy,
        entity.GroupId is null ? null : Guid.Parse(entity.GroupId),
        DeserializeTags(entity.TagsJson),
        entity.Remark,
        entity.IsEnabled,
        entity.PanelVersion,
        entity.Capabilities,
        (NodeHealth)entity.HealthStatus,
        entity.HealthMessage,
        ParseDate(entity.LastCheckedAt),
        ParseDate(entity.LastSeenAt),
        ParseDate(entity.CreatedAt) ?? DateTimeOffset.UtcNow,
        ParseDate(entity.UpdatedAt) ?? DateTimeOffset.UtcNow);

    public static NodeEntity ToEntity(NodeRecord record)
    {
        var entity = new NodeEntity();
        Apply(record, entity);
        return entity;
    }

    public static void Apply(NodeRecord record, NodeEntity entity)
    {
        entity.Id = record.Id.Value.ToString("D", CultureInfo.InvariantCulture);
        entity.Name = record.Name;
        entity.BaseUrl = record.BaseUrl.ToString();
        entity.SecurityEntrance = record.SecurityEntrance;
        entity.ApiKeyProtected = record.ApiKeyProtected;
        entity.ApiKeyHint = record.ApiKeyHint;
        entity.AuthMode = (int)record.AuthMode;
        entity.DetectedAuthMode = record.DetectedAuthMode is { } detected ? (int)detected : null;
        entity.ClockOffsetSeconds = record.ClockOffsetSeconds;
        entity.TlsPolicy = (int)record.TlsPolicy;
        entity.GroupId = record.GroupId?.ToString("D", CultureInfo.InvariantCulture);
        entity.TagsJson = JsonSerializer.Serialize(record.Tags, TagsJson);
        entity.Remark = record.Remark;
        entity.IsEnabled = record.IsEnabled;
        entity.PanelVersion = record.PanelVersion;
        entity.Capabilities = record.Capabilities;
        entity.HealthStatus = (int)record.Health;
        entity.HealthMessage = record.HealthMessage;
        entity.LastCheckedAt = FormatDate(record.LastCheckedAt);
        entity.LastSeenAt = FormatDate(record.LastSeenAt);
        entity.CreatedAt = FormatDate(record.CreatedAt) ?? string.Empty;
        entity.UpdatedAt = FormatDate(record.UpdatedAt) ?? string.Empty;
    }

    public static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string? FormatDate(DateTimeOffset? value) =>
        value is { } date ? FormatDate(date) : null;

    public static DateTimeOffset? ParseDate(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static IReadOnlyList<string> DeserializeTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, TagsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
