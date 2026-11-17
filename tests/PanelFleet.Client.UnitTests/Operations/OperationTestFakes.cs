using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Operations;

internal sealed class FakeNodeStore : INodeStore
{
    public List<NodeRecord> Nodes { get; } = [];

    public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeRecord>>(Nodes);

    public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeId>>(Nodes.Where(node => node.IsEnabled).Select(node => node.Id).ToArray());

    public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
        Task.FromResult(Nodes.FirstOrDefault(node => node.Id == id));

    public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task AddAsync(NodeRecord node, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeSecretProtector : INodeSecretProtector
{
    public ProtectedSecret Protect(string apiKey) => new("cipher:" + apiKey, "hint");

    public string Unprotect(string cipherText) => "fake-api-key";
}

internal sealed class FakePanelClientFactory : IPanelClientFactory
{
    public IPanelClient Create(NodeDescriptor node) => new FakePanelClient(node);

    private sealed class FakePanelClient : IPanelClient
    {
        public FakePanelClient(NodeDescriptor node) => Node = node;

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<T>(default, null, TimeSpan.Zero));

        public Task<PanelCallResult<T>> PostAsync<T>(string relativePath, object? body, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<T>(default, null, TimeSpan.Zero));

        public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<OsInfoDto>(null, null, TimeSpan.Zero));

        public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<DashboardCurrentDto>(null, null, TimeSpan.Zero));

        public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
            string ioOption = "all",
            string netOption = "all",
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<DashboardBaseDto>(null, null, TimeSpan.Zero));

        public void Dispose()
        {
        }
    }
}

internal sealed class FakeAuditStore : IAuditStore
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AddAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        lock (Entries)
        {
            Entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAsync(
        int take,
        string? action,
        NodeId? nodeId,
        CancellationToken cancellationToken)
    {
        lock (Entries)
        {
            var query = Entries.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(action))
            {
                query = query.Where(entry => entry.Action == action);
            }

            if (nodeId is { } id)
            {
                query = query.Where(entry => entry.NodeId == id);
            }

            return Task.FromResult<IReadOnlyList<AuditEntry>>(query.Take(take).ToArray());
        }
    }
}

internal sealed class RecordingOperationHandler : IOperationHandler
{
    public string Kind { get; set; } = "test.run";

    public OperationRisk Risk { get; set; } = OperationRisk.Low;

    public string? TypedConfirmationText { get; set; }

    public string? TargetType { get; set; } = "test";

    public List<string> ExecutedNodes { get; } = [];

    public Func<OperationNodeContext, CancellationToken, Task<OperationStepResult>>? Execute { get; set; }

    public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken)
    {
        var targets = context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray();
        return Task.FromResult(new OperationPlan(
            "测试操作",
            $"对 {targets.Length} 个节点执行测试操作。",
            Risk,
            targets,
            [],
            TypedConfirmationText,
            TargetType));
    }

    public async Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken)
    {
        ExecutedNodes.Add(context.Node.Name);

        if (Execute is { } execute)
        {
            return await execute(context, cancellationToken).ConfigureAwait(false);
        }

        return new OperationStepResult(
            context.Node.Id,
            context.Node.Name,
            true,
            "执行成功",
            null,
            null,
            null,
            TimeSpan.Zero,
            TargetId: "target-" + context.Node.Name);
    }
}

internal static class OperationTestData
{
    public static NodeRecord Node(string name, bool enabled = true) => new(
        NodeId.New(),
        name,
        new Uri("http://127.0.0.1:1000"),
        null,
        "cipher",
        "hint",
        AuthMode.HmacSha256,
        null,
        null,
        TlsPolicy.Strict,
        null,
        [],
        null,
        enabled,
        "v2.3.2",
        0,
        NodeHealth.Online,
        null,
        null,
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    public static ActorInfo Actor(string subject = "user-1") =>
        new(subject, "Alice", "alice@example.com", "127.0.0.1", "trace-1");
}
