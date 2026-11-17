using PanelFleet.Application.Models;

namespace PanelFleet.Application.Abstractions;

public interface INodeGroupStore
{
    Task<IReadOnlyList<NodeGroupSummary>> ListAsync(CancellationToken cancellationToken);

    Task<NodeGroupSummary?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken);

    Task AddAsync(NodeGroupSummary group, CancellationToken cancellationToken);

    Task UpdateAsync(NodeGroupSummary group, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
