using Microsoft.EntityFrameworkCore;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Infrastructure.Persistence;
using PanelFleet.Infrastructure.Persistence.Entities;

namespace PanelFleet.Infrastructure.Repositories;

public sealed class NodeGroupStore : INodeGroupStore
{
    private readonly PanelFleetDbContext _db;

    public NodeGroupStore(PanelFleetDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<NodeGroupSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var entities = await _db.NodeGroups
            .AsNoTracking()
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(ToSummary).ToArray();
    }

    public async Task<NodeGroupSummary?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = id.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var entity = await _db.NodeGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(group => group.Id == key, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToSummary(entity);
    }

    public Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var exceptKey = exceptId?.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        return _db.NodeGroups.AnyAsync(
            group => group.Name == name && (exceptKey == null || group.Id != exceptKey),
            cancellationToken);
    }

    public async Task AddAsync(NodeGroupSummary group, CancellationToken cancellationToken)
    {
        _db.NodeGroups.Add(ToEntity(group));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(NodeGroupSummary group, CancellationToken cancellationToken)
    {
        var key = group.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var entity = await _db.NodeGroups
            .FirstOrDefaultAsync(item => item.Id == key, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"分组 {key} 不存在。");

        entity.Name = group.Name;
        entity.SortOrder = group.SortOrder;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = id.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var entity = await _db.NodeGroups
            .FirstOrDefaultAsync(item => item.Id == key, cancellationToken)
            .ConfigureAwait(false);

        if (entity is not null)
        {
            _db.NodeGroups.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static NodeGroupSummary ToSummary(NodeGroupEntity entity) =>
        new(Guid.Parse(entity.Id), entity.Name, entity.SortOrder);

    private static NodeGroupEntity ToEntity(NodeGroupSummary group) => new()
    {
        Id = group.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
        Name = group.Name,
        SortOrder = group.SortOrder,
        CreatedAt = NodeMapper.FormatDate(DateTimeOffset.UtcNow)
    };
}
