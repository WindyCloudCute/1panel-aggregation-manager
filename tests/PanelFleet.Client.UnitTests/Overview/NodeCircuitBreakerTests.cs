using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Overview;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Overview;

public sealed class NodeCircuitBreakerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 6, 0, 0, TimeSpan.Zero);

    private static NodeCircuitBreaker CreateBreaker(int threshold = 2, int breakSeconds = 30) => new(new OverviewOptions
    {
        Circuit = new CircuitOptions { FailureThreshold = threshold, BreakSeconds = breakSeconds }
    });

    [Fact]
    public void InitiallyClosed_AndAllowsEnter()
    {
        var breaker = CreateBreaker();
        var id = NodeId.New();

        Assert.Equal(CircuitState.Closed, breaker.GetStatus(id, Now).State);
        Assert.True(breaker.TryEnter(id, Now, force: false));
    }

    [Fact]
    public void ConsecutiveFailures_OpenCircuit_AndSkip()
    {
        var breaker = CreateBreaker(threshold: 2);
        var id = NodeId.New();

        breaker.RecordFailure(id, PanelErrorKind.Network, Now);
        Assert.Equal(CircuitState.Closed, breaker.GetStatus(id, Now).State);

        breaker.RecordFailure(id, PanelErrorKind.Network, Now);
        var status = breaker.GetStatus(id, Now);
        Assert.Equal(CircuitState.Open, status.State);
        Assert.False(breaker.TryEnter(id, Now, force: false));
        Assert.True(breaker.TryEnter(id, Now, force: true));
    }

    [Fact]
    public void HalfOpen_AllowsSingleProbe_ThenSuccessCloses()
    {
        var breaker = CreateBreaker(threshold: 1);
        var id = NodeId.New();

        breaker.RecordFailure(id, PanelErrorKind.Timeout, Now);
        var later = Now.AddSeconds(31);

        Assert.Equal(CircuitState.HalfOpen, breaker.GetStatus(id, later).State);
        Assert.True(breaker.TryEnter(id, later, force: false));
        Assert.False(breaker.TryEnter(id, later, force: false));

        breaker.RecordSuccess(id);
        Assert.Equal(CircuitState.Closed, breaker.GetStatus(id, later).State);
        Assert.True(breaker.TryEnter(id, later, force: false));
    }

    [Fact]
    public void HalfOpenProbeFailure_ReopensCircuit()
    {
        var breaker = CreateBreaker(threshold: 1);
        var id = NodeId.New();

        breaker.RecordFailure(id, PanelErrorKind.Network, Now);
        var later = Now.AddSeconds(31);

        Assert.True(breaker.TryEnter(id, later, force: false));
        breaker.RecordFailure(id, PanelErrorKind.Network, later);

        Assert.Equal(CircuitState.Open, breaker.GetStatus(id, later).State);
        Assert.False(breaker.TryEnter(id, later.AddSeconds(1), force: false));
    }

    [Fact]
    public void NonTrippingKinds_DoNotOpenCircuit()
    {
        var breaker = CreateBreaker(threshold: 1);
        var id = NodeId.New();

        for (var index = 0; index < 5; index++)
        {
            breaker.RecordFailure(id, PanelErrorKind.Unauthorized, Now);
            breaker.RecordFailure(id, PanelErrorKind.Validation, Now);
        }

        Assert.Equal(CircuitState.Closed, breaker.GetStatus(id, Now).State);
        Assert.True(breaker.TryEnter(id, Now, force: false));
    }

    [Fact]
    public void Success_ResetsFailureCount()
    {
        var breaker = CreateBreaker(threshold: 2);
        var id = NodeId.New();

        breaker.RecordFailure(id, PanelErrorKind.Network, Now);
        breaker.RecordSuccess(id);
        breaker.RecordFailure(id, PanelErrorKind.Network, Now);

        Assert.Equal(CircuitState.Closed, breaker.GetStatus(id, Now).State);
        Assert.Equal(1, breaker.GetStatus(id, Now).ConsecutiveFailures);
    }
}
