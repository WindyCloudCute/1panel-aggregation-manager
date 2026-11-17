using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Abstractions;

public interface INodeService
{
    Task<IReadOnlyList<NodeSummary>> ListAsync(CancellationToken cancellationToken);

    Task<NodeSummary> CreateAsync(CreateNodeRequest request, ActorInfo actor, CancellationToken cancellationToken);

    Task<NodeSummary> UpdateAsync(NodeId id, UpdateNodeRequest request, ActorInfo actor, CancellationToken cancellationToken);

    Task DeleteAsync(NodeId id, ActorInfo actor, CancellationToken cancellationToken);

    Task<NodeTestResult> TestSavedAsync(NodeId id, ActorInfo actor, CancellationToken cancellationToken);

    Task<NodeTestResult> TestDraftAsync(TestDraftRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken);

    Task<NodeGroupSummary> CreateGroupAsync(GroupCreateRequest request, ActorInfo actor, CancellationToken cancellationToken);

    Task<NodeGroupSummary> UpdateGroupAsync(Guid id, GroupUpdateRequest request, ActorInfo actor, CancellationToken cancellationToken);

    Task DeleteGroupAsync(Guid id, ActorInfo actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken);

    Task CheckNodeHealthAsync(NodeId id, CancellationToken cancellationToken);
}
