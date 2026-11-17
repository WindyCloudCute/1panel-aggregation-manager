using Microsoft.EntityFrameworkCore;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;
using PanelFleet.Infrastructure.Persistence;
using PanelFleet.Infrastructure.Persistence.Entities;

namespace PanelFleet.Infrastructure.Repositories;

public sealed class AuditStore : IAuditStore
{
    private readonly PanelFleetDbContext _db;

    public AuditStore(PanelFleetDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        _db.AuditEvents.Add(new AuditEventEntity
        {
            Id = entry.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            OccurredAt = NodeMapper.FormatDate(entry.OccurredAt),
            ActorSubject = entry.Actor.Subject,
            ActorName = entry.Actor.Name,
            ActorEmail = entry.Actor.Email,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            NodeId = entry.NodeId?.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            DetailJson = entry.DetailJson,
            ClientIp = entry.Actor.ClientIp,
            TraceId = entry.Actor.TraceId
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(
        int take,
        string? action,
        NodeId? nodeId,
        CancellationToken cancellationToken)
    {
        var query = _db.AuditEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(item => item.Action == action);
        }

        if (nodeId is { } id)
        {
            var key = id.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
            query = query.Where(item => item.NodeId == key);
        }

        var entities = await query
            .OrderByDescending(item => item.OccurredAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(ToEntry).ToArray();
    }

    private static AuditEntry ToEntry(AuditEventEntity entity) => new(
        Guid.Parse(entity.Id),
        NodeMapper.ParseDate(entity.OccurredAt) ?? DateTimeOffset.UtcNow,
        new ActorInfo(entity.ActorSubject, entity.ActorName, entity.ActorEmail, entity.ClientIp, entity.TraceId),
        entity.Action,
        entity.TargetType,
        entity.TargetId,
        entity.NodeId is null ? null : new NodeId(Guid.Parse(entity.NodeId)),
        entity.DetailJson);
}
