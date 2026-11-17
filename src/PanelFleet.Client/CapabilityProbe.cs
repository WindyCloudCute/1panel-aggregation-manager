using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>通过最轻量的 dashboard 接口验证节点可达性与鉴权。</summary>
public sealed class CapabilityProbe
{
    public async Task<CapabilityProbeResult> ProbeAsync(
        IPanelClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var result = await client.GetOsInfoAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var data = result.Data;
            var os = data?.Os ?? data?.Platform;
            var version = await TryGetVersionAsync(client, cancellationToken).ConfigureAwait(false);
            return new CapabilityProbeResult(
                Reachable: true,
                Authorized: true,
                PanelVersion: version,
                OsDescription: os,
                Error: null,
                Elapsed: result.Elapsed);
        }

        var error = result.Error;
        var reachable = error is not null
            && error.Kind is not (PanelErrorKind.Network
                or PanelErrorKind.Tls
                or PanelErrorKind.Timeout
                or PanelErrorKind.Canceled);

        return new CapabilityProbeResult(
            Reachable: reachable,
            Authorized: false,
            PanelVersion: null,
            OsDescription: null,
            Error: error,
            Elapsed: result.Elapsed);
    }

    private static async Task<string?> TryGetVersionAsync(IPanelClient client, CancellationToken cancellationToken)
    {
        try
        {
            var versionResult = await client
                .PostAsync<Dto.SettingsSearchDto>(PanelRoutes.SettingsSearch, new { }, cancellationToken)
                .ConfigureAwait(false);

            return versionResult.IsSuccess ? versionResult.Data?.SystemVersion : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
