using PanelFleet.Application.Models;

namespace PanelFleet.Application.Abstractions;

public interface IConnectionTester
{
    Task<NodeTestResult> TestAsync(NodeConnectionInput input, CancellationToken cancellationToken);
}
