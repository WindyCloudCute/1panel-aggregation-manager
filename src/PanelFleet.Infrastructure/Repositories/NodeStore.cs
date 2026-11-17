using Microsoft.EntityFrameworkCore;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;
using PanelFleet.Infrastructure.Persistence;

namespace PanelFleet.Infrastructure.Repositories;

public sealed class NodeStore : INodeStore
{
    private readonly PanelFleetDbContext _db;

    public NodeStore(PanelFleetDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var entities = await _db.Nodes
            .AsNoTracking()
            .OrderBy(node => node.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(NodeMapper.ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await _db.Nodes
            .AsNoTracking()
            .Where(node => node.IsEnabled)
            .OrderBy(node => node.Name)
            .Select(node => node.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ids.Select(id => new NodeId(Guid.Parse(id))).ToArray();
    }

    public async Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken)
    {
        var key = id.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var entity = await _db.Nodes
            .AsNoTracking()
            .FirstOrDefaultAsync(node => node.Id == key, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : NodeMapper.ToRecord(entity);
    }

    public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken)
    {
        var exceptKey = exceptId?.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        return _db.Nodes.AnyAsync(
            node => node.Name == name && (exceptKey == null || node.Id != exceptKey),
            cancellationToken);
    }

    public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken)
    {
        var key = baseUrl.ToString();
        var exceptKey = exceptId?.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        return _db.Nodes.AnyAsync(
            node => node.BaseUrl == key && (exceptKey == null || node.Id != exceptKey),
            cancellationToken);
    }

    public async Task AddAsync(NodeRecord node, CancellationToken cancellationToken)
    {
        _db.Nodes.Add(NodeMapper.ToEntity(node));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken)
    {
        var key = node.Id.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var entity = await _db.Nodes
            .FirstOrDefaultAsync(item => item.Id == key, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"节点 {key} 不存在。");

        NodeMapper.Apply(node, entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(NodeId id, CancellationToken cancellationToken)
    {
        var key = id.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var entity = await _db.Nodes
            .FirstOrDefaultAsync(item => item.Id == key, cancellationToken)
            .ConfigureAwait(false);

        if (entity is not null)
        {
            _db.Nodes.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
