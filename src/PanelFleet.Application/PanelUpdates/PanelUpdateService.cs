using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Updates;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.PanelUpdates;

/// <summary>面板版本检查与升级预检（只读）：不触发升级、不修改节点，只访问 1Panel 查询接口。</summary>
public interface IPanelUpdateService
{
    /// <summary>检查节点当前版本、可用版本与更新说明；<paramref name="nodeIds"/> 为空表示全部节点。</summary>
    Task<PanelVersionCheckResult> CheckVersionsAsync(
        IReadOnlyList<NodeId>? nodeIds,
        CancellationToken cancellationToken);

    /// <summary>升级预检：在线、版本可读、存在更新、磁盘空间足够；返回逐节点阻断原因。</summary>
    Task<PanelUpdatePrecheckResult> PrecheckAsync(
        IReadOnlyList<NodeId> nodeIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// 面板版本检查服务：当前版本来自 <c>POST /api/v2/core/settings/search/base</c> 的 <c>data.systemVersion</c>
/// （该 POST 是只读查询、无副作用），可用版本/更新说明来自 <c>GET /api/v2/core/settings/upgrade</c>。
/// 磁盘空间来自 <c>GET /api/v2/dashboard/base/all/all</c> 的 <c>currentInfo.diskData</c>。
/// </summary>
public sealed class PanelUpdateService : IPanelUpdateService
{
    private readonly INodeStore _nodes;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly PanelUpdateOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PanelUpdateService> _logger;

    public PanelUpdateService(
        INodeStore nodes,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        PanelUpdateOptions options,
        TimeProvider time,
        ILogger<PanelUpdateService> logger)
    {
        _nodes = nodes;
        _protector = protector;
        _clientFactory = clientFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<PanelVersionCheckResult> CheckVersionsAsync(
        IReadOnlyList<NodeId>? nodeIds,
        CancellationToken cancellationToken)
    {
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<NodeRecord> selected;
        if (nodeIds is { Count: > 0 })
        {
            var order = nodeIds
                .Distinct()
                .Select((id, index) => (id, index))
                .ToDictionary(pair => pair.id, pair => pair.index);
            selected = records
                .Where(record => order.ContainsKey(record.Id))
                .OrderBy(record => order[record.Id])
                .ToArray();
        }
        else
        {
            selected = records;
        }

        var checks = selected.Select(record => CheckNodeAsync(record, cancellationToken));
        var nodes = await Task.WhenAll(checks).ConfigureAwait(false);
        return new PanelVersionCheckResult(_time.GetUtcNow(), nodes);
    }

    public async Task<PanelUpdatePrecheckResult> PrecheckAsync(
        IReadOnlyList<NodeId> nodeIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);

        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var byId = records.ToDictionary(record => record.Id);
        var items = new List<PanelUpdatePrecheckItem>(nodeIds.Count);

        foreach (var nodeId in nodeIds.Distinct())
        {
            if (!byId.TryGetValue(nodeId, out var record))
            {
                items.Add(new PanelUpdatePrecheckItem(
                    nodeId,
                    nodeId.Value.ToString(),
                    null,
                    null,
                    false,
                    null,
                    ["节点不存在或已被删除。"],
                    null));
                continue;
            }

            var info = await CheckNodeAsync(record, cancellationToken).ConfigureAwait(false);
            items.Add(new PanelUpdatePrecheckItem(
                info.NodeId,
                info.NodeName,
                info.CurrentVersion,
                info.AvailableVersion,
                info.CanUpgrade,
                info.DiskFreeBytes,
                info.BlockingReasons,
                info.Error));
        }

        return new PanelUpdatePrecheckResult(_time.GetUtcNow(), items);
    }

    private async Task<PanelNodeVersionInfo> CheckNodeAsync(NodeRecord record, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var reasons = new List<string>();
        var warnings = new List<string>();

        if (!record.IsEnabled)
        {
            return new PanelNodeVersionInfo(
                record.Id,
                record.Name,
                NodeEnabled: false,
                Online: false,
                VersionReadable: false,
                CurrentVersion: null,
                AvailableVersion: null,
                ReleaseNote: null,
                UpdateAvailable: false,
                AlreadyLatest: false,
                DiskFreeBytes: null,
                DiskPath: null,
                ["节点已禁用，不参与版本检查与升级。"],
                [],
                null,
                stopwatch.Elapsed);
        }

        using var client = _clientFactory.Create(BuildDescriptor(record));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.CheckTimeoutSeconds));
        var token = timeoutCts.Token;

        DashboardBaseDto? dashboard = null;
        PanelError? healthError = null;
        try
        {
            var health = await client.GetDashboardBaseAsync("all", "all", token).ConfigureAwait(false);
            if (health.Error is null)
            {
                dashboard = health.Data;
            }
            else
            {
                healthError = health.Error;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            healthError = TimeoutError(record.Name);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            healthError = ProtocolError(record.Name, exception.Message);
            _logger.LogWarning(exception, "面板更新检查失败 节点 {NodeId}", record.Id);
        }

        if (healthError is not null)
        {
            return new PanelNodeVersionInfo(
                record.Id,
                record.Name,
                NodeEnabled: true,
                Online: false,
                VersionReadable: false,
                CurrentVersion: null,
                AvailableVersion: null,
                ReleaseNote: null,
                UpdateAvailable: false,
                AlreadyLatest: false,
                DiskFreeBytes: null,
                DiskPath: null,
                [$"节点不可达，无法检查版本（{healthError.Message}）。"],
                [],
                healthError,
                stopwatch.Elapsed);
        }

        var (currentVersion, currentError) = await ReadCurrentVersionAsync(client, record.Name, token)
            .ConfigureAwait(false);
        if (currentError is not null)
        {
            reasons.Add($"无法读取当前版本（{currentError.Message}）。");
        }

        var (upgradeInfo, upgradeError) = await ReadUpgradeInfoAsync(client, record.Name, token)
            .ConfigureAwait(false);
        if (upgradeError is not null)
        {
            reasons.Add($"无法读取可用版本（{upgradeError.Message}）。");
        }

        var availableVersion = ResolveAvailableVersion(upgradeInfo);
        var currentParsable = PanelVersionComparer.TryParse(currentVersion, out _);
        if (currentError is null && !string.IsNullOrWhiteSpace(currentVersion) && !currentParsable)
        {
            reasons.Add($"当前版本格式无法识别（{currentVersion}），已阻止升级。");
        }

        var versionReadable = currentError is null
            && !string.IsNullOrWhiteSpace(currentVersion)
            && currentParsable;

        var alreadyLatest = false;
        var updateAvailable = false;
        if (currentError is null && !string.IsNullOrWhiteSpace(currentVersion) && currentParsable && upgradeError is null)
        {
            if (availableVersion is null)
            {
                // 1Panel 的约定：newVersion 与 latestVersion 都为空 = 当前已是最新（或离线/开发模式）。
                alreadyLatest = true;
            }
            else
            {
                updateAvailable = PanelVersionComparer.IsUpgrade(availableVersion, currentVersion);
                alreadyLatest = !updateAvailable;
            }
        }

        if (alreadyLatest)
        {
            reasons.Add($"已是最新版本（当前 {currentVersion ?? "未知"}）。");
        }

        var (diskFreeBytes, diskPath, diskError) = ReadDisk(dashboard);
        if (diskError is not null)
        {
            // 磁盘信息读不到不阻断升级（1Panel 升级程序会在服务端自行校验空间），只作为提示。
            warnings.Add(diskError);
        }
        else if (diskFreeBytes is { } freeBytes && freeBytes < _options.MinimumFreeDiskBytes)
        {
            reasons.Add(
                $"磁盘可用空间不足：{diskPath ?? "/"} 仅 {FormatMegabytes(freeBytes)}，1Panel 升级要求至少 {FormatMegabytes(_options.MinimumFreeDiskBytes)}。");
        }

        return new PanelNodeVersionInfo(
            record.Id,
            record.Name,
            NodeEnabled: true,
            Online: true,
            VersionReadable: versionReadable,
            CurrentVersion: currentVersion,
            AvailableVersion: availableVersion,
            ReleaseNote: upgradeInfo?.ReleaseNote,
            UpdateAvailable: updateAvailable,
            AlreadyLatest: alreadyLatest,
            DiskFreeBytes: diskFreeBytes,
            DiskPath: diskPath,
            reasons,
            warnings,
            currentError ?? upgradeError,
            stopwatch.Elapsed);
    }

    private static async Task<(string? Version, PanelError? Error)> ReadCurrentVersionAsync(
        IPanelClient client,
        string nodeName,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client
                .PostAsync<PanelSettingBaseDto>(UpdateRoutes.PanelSettingBase, new { }, cancellationToken)
                .ConfigureAwait(false);

            if (result.Error is not null)
            {
                return (null, result.Error);
            }

            var version = result.Data?.SystemVersion?.Trim();
            return string.IsNullOrWhiteSpace(version)
                ? (null, ProtocolError(nodeName, "响应缺少 systemVersion 字段。"))
                : (version, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, TimeoutError(nodeName));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, ProtocolError(nodeName, exception.Message));
        }
    }

    private static async Task<(PanelUpgradeInfoDto? Info, PanelError? Error)> ReadUpgradeInfoAsync(
        IPanelClient client,
        string nodeName,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client
                .GetAsync<PanelUpgradeInfoDto>(UpdateRoutes.UpgradeInfo, cancellationToken)
                .ConfigureAwait(false);

            return result.Error is not null ? (null, result.Error) : (result.Data, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, TimeoutError(nodeName));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, ProtocolError(nodeName, exception.Message));
        }
    }

    private static string? ResolveAvailableVersion(PanelUpgradeInfoDto? info)
    {
        if (info is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(info.LatestVersion))
        {
            return info.LatestVersion.Trim();
        }

        return string.IsNullOrWhiteSpace(info.NewVersion) ? null : info.NewVersion.Trim();
    }

    private (long? FreeBytes, string? Path, string? Warning) ReadDisk(DashboardBaseDto? dashboard)
    {
        var disks = dashboard?.CurrentInfo?.DiskData;
        if (disks is not { Count: > 0 })
        {
            return (null, null, "未获取到磁盘信息，升级前请自行确认安装目录可用空间（1Panel 要求 ≥ 500 MiB）。");
        }

        var disk = disks.FirstOrDefault(item => string.Equals(item.Path, "/", StringComparison.Ordinal))
            ?? disks.OrderByDescending(item => item.Free).First();

        return disk.Free > 0
            ? (disk.Free, disk.Path, null)
            : (null, disk.Path, "磁盘可用空间为 0 或不可读，升级前请自行确认安装目录可用空间。");
    }

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    private static PanelError TimeoutError(string nodeName) => new(
        PanelErrorKind.Timeout,
        $"[{nodeName}] 版本检查超时。");

    private static PanelError ProtocolError(string nodeName, string detail) => new(
        PanelErrorKind.Protocol,
        $"[{nodeName}] {detail}");

    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
}
