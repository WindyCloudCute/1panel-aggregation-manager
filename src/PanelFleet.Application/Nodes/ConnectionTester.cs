using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Nodes;

/// <summary>通过最轻量接口验证节点可达性与鉴权。</summary>
public sealed class ConnectionTester : IConnectionTester
{
    private readonly IPanelClientFactory _clientFactory;
    private readonly ILogger<ConnectionTester> _logger;

    public ConnectionTester(IPanelClientFactory clientFactory, ILogger<ConnectionTester> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<NodeTestResult> TestAsync(NodeConnectionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var stopwatch = Stopwatch.StartNew();
        using var client = _clientFactory.Create(new NodeDescriptor(
            NodeId.New(),
            input.Name,
            input.BaseUrl,
            input.ApiKey,
            input.AuthMode,
            input.TlsPolicy,
            ClockOffset: input.ClockOffset));

        var probe = new CapabilityProbe();
        var probeResult = await probe.ProbeAsync(client, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var auth = client.AuthState;

        if (probeResult.Error is not null)
        {
            _logger.LogInformation(
                "节点 {NodeId} 连接测试失败: {Kind}",
                client.Node.Id,
                probeResult.Error.Kind);
        }

        return new NodeTestResult(
            Reachable: probeResult.Reachable,
            Authorized: probeResult.Authorized,
            LatencyMs: (long)stopwatch.Elapsed.TotalMilliseconds,
            DetectedAuthMode: auth.Resolved ? auth.EffectiveMode : null,
            ClockOffsetSeconds: auth.Resolved ? auth.ClockOffset.TotalSeconds : null,
            PanelVersion: probeResult.PanelVersion,
            Os: probeResult.OsDescription,
            ErrorKind: probeResult.Error?.Kind,
            ErrorMessage: probeResult.Error?.Message);
    }
}
