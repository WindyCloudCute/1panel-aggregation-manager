using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

public interface INodeStore
{
    Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken);

    Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken);

    Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken);

    Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken);

    Task AddAsync(NodeRecord node, CancellationToken cancellationToken);

    Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken);

    Task DeleteAsync(NodeId id, CancellationToken cancellationToken);
}
