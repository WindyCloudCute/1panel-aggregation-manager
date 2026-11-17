using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>集成测试中不访问真实 1Panel。</summary>
public sealed class FakeConnectionTester : IConnectionTester
{
    public Task<NodeTestResult> TestAsync(NodeConnectionInput input, CancellationToken cancellationToken) =>
        Task.FromResult(new NodeTestResult(
            Reachable: true,
            Authorized: true,
            LatencyMs: 12,
            DetectedAuthMode: AuthMode.HmacSha256,
            ClockOffsetSeconds: 0,
            PanelVersion: "2.0.15",
            Os: "Ubuntu",
            ErrorKind: null,
            ErrorMessage: null));
}
