using PanelFleet.Application.Models;
using PanelFleet.Application.PanelUpdates;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Updates;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.PanelUpdates;

/// <summary>按节点名脚本化的假 1Panel 客户端工厂：覆盖版本检查、升级提交与升级后健康检查。</summary>
internal sealed class ScriptedPanelClientFactory : IPanelClientFactory
{
    private readonly Dictionary<string, PanelUpdateNodeScript> _scripts = new(StringComparer.OrdinalIgnoreCase);

    public PanelUpdateNodeScript Script(string nodeName)
    {
        if (!_scripts.TryGetValue(nodeName, out var script))
        {
            script = new PanelUpdateNodeScript(nodeName);
            _scripts[nodeName] = script;
        }

        return script;
    }

    public IPanelClient Create(NodeDescriptor node) => new ScriptedPanelClient(Script(node.Name));

    private sealed class ScriptedPanelClient : IPanelClient
    {
        private readonly PanelUpdateNodeScript _script;
        private readonly List<string> _upgraded = [];

        public ScriptedPanelClient(PanelUpdateNodeScript script)
        {
            _script = script;
            Node = new NodeDescriptor(NodeId.New(), script.Name, new Uri("http://127.0.0.1:1000"), "fake-key");
        }

        public NodeDescriptor Node { get; }

        public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

        public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default)
        {
            if (relativePath == UpdateRoutes.UpgradeInfo && typeof(T) == typeof(PanelUpgradeInfoDto))
            {
                if (!_script.UpgradeInfoReadable)
                {
                    return Task.FromResult(Failure<T>(PanelErrorKind.PanelServerError, "读取可用版本失败"));
                }

                var dto = new PanelUpgradeInfoDto
                {
                    TestVersion = _script.TestVersion,
                    NewVersion = _script.NewVersion,
                    LatestVersion = _script.LatestVersion,
                    ReleaseNote = _script.ReleaseNote
                };
                return Task.FromResult(new PanelCallResult<T>((T)(object)dto, null, TimeSpan.Zero));
            }

            return Task.FromResult(Failure<T>(PanelErrorKind.NotFound, "假客户端未实现路径：" + relativePath));
        }

        public Task<PanelCallResult<T>> PostAsync<T>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default)
        {
            if (relativePath == UpdateRoutes.PanelSettingBase && typeof(T) == typeof(PanelSettingBaseDto))
            {
                if (!_script.CurrentVersionReadable)
                {
                    return Task.FromResult(Failure<T>(PanelErrorKind.PanelServerError, "读取当前版本失败"));
                }

                var version = _upgraded.Count > 0
                    ? _script.VersionAfterUpgrade ?? _script.CurrentVersion
                    : _script.CurrentVersion;
                var dto = new PanelSettingBaseDto { SystemVersion = version };
                return Task.FromResult(new PanelCallResult<T>((T)(object)dto, null, TimeSpan.Zero));
            }

            if (relativePath == UpdateRoutes.UpgradeExecute)
            {
                var version = ExtractVersion(body);
                _script.SubmittedVersions.Add(version);
                if (!_script.UpgradeSubmitSucceeds)
                {
                    return Task.FromResult(Failure<T>(PanelErrorKind.PanelServerError, "下载升级包失败"));
                }

                _upgraded.Add(version);
                return Task.FromResult(new PanelCallResult<T>(default, null, TimeSpan.Zero));
            }

            return Task.FromResult(Failure<T>(PanelErrorKind.NotFound, "假客户端未实现路径：" + relativePath));
        }

        public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<OsInfoDto>(null, null, TimeSpan.Zero));

        public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelCallResult<DashboardCurrentDto>(null, null, TimeSpan.Zero));

        public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
            string ioOption = "all",
            string netOption = "all",
            CancellationToken cancellationToken = default)
        {
            _script.HealthCalls++;
            var reachable = _upgraded.Count == 0
                ? _script.DashboardReachable
                : _script.DashboardReachableAfterUpgrade;
            if (!reachable)
            {
                return Task.FromResult(new PanelCallResult<DashboardBaseDto>(
                    null,
                    new PanelError(PanelErrorKind.Network, $"[{_script.Name}] 节点不可达"),
                    TimeSpan.Zero));
            }

            var dto = new DashboardBaseDto
            {
                Hostname = _script.Name,
                CurrentInfo = new DashboardCurrentDto
                {
                    DiskData =
                    [
                        new DiskInfoDto
                        {
                            Path = "/",
                            Total = 100L * 1024 * 1024 * 1024,
                            Free = _script.DiskFreeBytes ?? 10L * 1024 * 1024 * 1024
                        }
                    ]
                }
            };
            return Task.FromResult(new PanelCallResult<DashboardBaseDto>(dto, null, TimeSpan.Zero));
        }

        public void Dispose()
        {
        }

        private static PanelCallResult<T> Failure<T>(PanelErrorKind kind, string message) =>
            new(default, new PanelError(kind, message), TimeSpan.Zero);

        private static string ExtractVersion(object? body)
        {
            if (body is null)
            {
                return string.Empty;
            }

            var property = body.GetType().GetProperty("version");
            return property?.GetValue(body) as string ?? string.Empty;
        }
    }
}

/// <summary>单个节点的假面板脚本（版本、可用更新、磁盘、升级结果）。</summary>
internal sealed class PanelUpdateNodeScript
{
    public PanelUpdateNodeScript(string name = "node") => Name = name;

    public string Name { get; set; }

    public string CurrentVersion { get; set; } = "v2.3.2";

    public string? LatestVersion { get; set; } = "v2.5.1";

    public string? NewVersion { get; set; }

    public string? TestVersion { get; set; }

    public string? ReleaseNote { get; set; } = "## 更新内容\n\n- 修复若干问题";

    public bool DashboardReachable { get; set; } = true;

    public bool DashboardReachableAfterUpgrade { get; set; } = true;

    public bool CurrentVersionReadable { get; set; } = true;

    public bool UpgradeInfoReadable { get; set; } = true;

    public long? DiskFreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public bool UpgradeSubmitSucceeds { get; set; } = true;

    /// <summary>升级后回读到的版本；为空则仍返回当前版本（模拟升级未生效）。</summary>
    public string? VersionAfterUpgrade { get; set; }

    public List<string> SubmittedVersions { get; } = [];

    public int HealthCalls { get; set; }
}

internal static class PanelUpdateTestData
{
    public static NodeRecord Node(string name, ScriptedPanelClientFactory factory, bool enabled = true)
    {
        factory.Script(name);
        return new NodeRecord(
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
    }

    public static ActorInfo Actor() =>
        new("user-1", "Alice", "alice@example.com", "127.0.0.1", "trace-1");
}
