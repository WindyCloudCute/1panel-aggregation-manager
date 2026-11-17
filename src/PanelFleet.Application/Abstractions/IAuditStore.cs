using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

public interface IAuditStore
{
    Task AddAsync(AuditEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEntry>> ListAsync(
        int take,
        string? action,
        NodeId? nodeId,
        CancellationToken cancellationToken);
}
