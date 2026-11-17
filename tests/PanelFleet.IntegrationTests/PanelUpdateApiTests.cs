using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Operations;
using PanelFleet.Application.PanelUpdates;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Updates;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// P12 面板更新集成测试（服务层：PanelFleetWebApplicationFactory + 自建假 IPanelClientFactory）：
/// 覆盖 版本检查 → 预览（Critical/强确认文本）→ 单节点/批量执行 → 失败停止 → 审计。
/// 全程只打内存假面板，<b>不会访问真实节点、不会真实升级</b>。
/// </summary>
public sealed class PanelUpdateApiTests
{
    [Fact]
    public async Task CheckVersions_ReadsCurrentAndAvailableVersion()
    {
        var panels = new ScriptedPanelClientFactory();
        using var factory = CreateFactory(panels);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "node-a");
        panels.Script("node-a").VersionAfterUpgrade = "v2.5.1";

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPanelUpdateService>();

        var result = await service.CheckVersionsAsync(null, CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.Equal("node-a", node.NodeName);
        Assert.Equal("v2.3.2", node.CurrentVersion);
        Assert.Equal("v2.5.1", node.AvailableVersion);
        Assert.True(node.UpdateAvailable);
        Assert.True(node.CanUpgrade);
        Assert.Equal(1, result.UpgradeableCount);
    }

    [Fact]
    public async Task VersionCheck_RejectsAlreadyLatestAndOfflineNodes()
    {
        var panels = new ScriptedPanelClientFactory();
        using var factory = CreateFactory(panels);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        await CreateNodeAsync(client, token, cookie, "up-to-date-node");
        await CreateNodeAsync(client, token, cookie, "offline-node");
        panels.Script("up-to-date-node").CurrentVersion = "v2.5.1";
        panels.Script("up-to-date-node").LatestVersion = "v2.5.1";
        panels.Script("offline-node").DashboardReachable = false;

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPanelUpdateService>();

        var check = await service.CheckVersionsAsync(null, CancellationToken.None);

        var upToDate = check.Nodes.Single(node => node.NodeName == "up-to-date-node");
        Assert.True(upToDate.AlreadyLatest);
        Assert.False(upToDate.CanUpgrade);

        var offline = check.Nodes.Single(node => node.NodeName == "offline-node");
        Assert.False(offline.Online);
        Assert.False(offline.CanUpgrade);
        Assert.Contains(offline.BlockingReasons, reason => reason.Contains("节点不可达", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_AlreadyLatestNode_ThrowsValidation()
    {
        var panels = new ScriptedPanelClientFactory();
        using var factory = CreateFactory(panels);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "up-to-date-node");
        panels.Script("up-to-date-node").CurrentVersion = "v2.5.1";
        panels.Script("up-to-date-node").LatestVersion = "v2.5.1";

        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();

        await Assert.ThrowsAsync<OperationValidationException>(
            () => operations.PreviewAsync(
                new OperationInvocation(
                    PanelUpdateOperationHandler.HandlerKind,
                    [new NodeId(nodeId)]),
                PanelUpdateTestActor(),
                CancellationToken.None));
    }

    [Fact]
    public async Task SingleNode_PreviewTypedConfirmation_Execute_AndAudit()
    {
        var panels = new ScriptedPanelClientFactory();
        using var factory = CreateFactory(panels);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "node-a");
        panels.Script("node-a").VersionAfterUpgrade = "v2.5.1";

        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();
        var actor = PanelUpdateTestActor();
        var invocation = new OperationInvocation(
            PanelUpdateOperationHandler.HandlerKind,
            [new NodeId(nodeId)]);

        var preview = await operations.PreviewAsync(invocation, actor, CancellationToken.None);

        Assert.Equal(OperationRisk.Critical, preview.Risk);
        Assert.True(preview.RequiresTypedConfirmation);
        Assert.Equal("node-a", preview.ConfirmText);

        // 错误确认文本会消费 Token，必须重新预览。
        await Assert.ThrowsAsync<OperationConfirmationException>(
            () => operations.ExecuteAsync(preview.TokenId, "错误文本", actor, CancellationToken.None));

        var retryPreview = await operations.PreviewAsync(invocation, actor, CancellationToken.None);
        var execution = await operations.ExecuteAsync(
            retryPreview.TokenId,
            retryPreview.ConfirmText,
            actor,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, execution.Status);
        var step = Assert.Single(execution.Steps);
        Assert.True(step.Success);
        Assert.Contains("v2.5.1", step.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "v2.5.1" }, panels.Script("node-a").SubmittedVersions);

        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var entries = await audit.ListAsync(50, "operation.executed", new NodeId(nodeId), CancellationToken.None);
        var entry = Assert.Single(entries);
        Assert.Equal("panel", entry.TargetType);
        Assert.Contains("v2.5.1", entry.DetailJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_ExecutesSequentially_StopsOnFailure_AndWritesBatchAudit()
    {
        var panels = new ScriptedPanelClientFactory();
        using var factory = CreateFactory(panels);
        using var client = factory.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeA = await CreateNodeAsync(client, token, cookie, "node-a");
        var nodeB = await CreateNodeAsync(client, token, cookie, "node-b");
        var nodeC = await CreateNodeAsync(client, token, cookie, "node-c");
        panels.Script("node-a").VersionAfterUpgrade = "v2.5.1";
        panels.Script("node-b").SubmitSucceeds = false;
        panels.Script("node-c").VersionAfterUpgrade = "v2.5.1";

        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWriteOperationService>();
        var actor = PanelUpdateTestActor();
        var invocation = new OperationInvocation(
            PanelUpdateOperationHandler.HandlerKind,
            [new NodeId(nodeA), new NodeId(nodeB), new NodeId(nodeC)]);

        var preview = await operations.PreviewAsync(invocation, actor, CancellationToken.None);

        Assert.Equal("升级 3 个节点", preview.ConfirmText);
        var execution = await operations.ExecuteAsync(
            preview.TokenId,
            preview.ConfirmText,
            actor,
            CancellationToken.None);

        Assert.Equal(OperationStatus.PartiallySucceeded, execution.Status);
        Assert.Equal(2, execution.Steps.Count);
        Assert.True(execution.Steps[0].Success);
        Assert.False(execution.Steps[1].Success);
        Assert.Equal(new[] { "v2.5.1" }, panels.Script("node-a").SubmittedVersions);
        Assert.Single(panels.Script("node-b").SubmittedVersions);
        Assert.Empty(panels.Script("node-c").SubmittedVersions);

        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var batchEntries = await audit.ListAsync(50, "operation.batch", null, CancellationToken.None);
        Assert.Single(batchEntries);
        Assert.Contains("\"nodeCount\":3", batchEntries[0].DetailJson!, StringComparison.Ordinal);
        Assert.Contains("\"succeeded\":1", batchEntries[0].DetailJson!, StringComparison.Ordinal);

        var failedEntries = await audit.ListAsync(50, "operation.failed", null, CancellationToken.None);
        Assert.Contains(failedEntries, entry => entry.NodeId == new NodeId(nodeB));
    }

    private static PanelFleetWebApplicationFactory CreateFactory(ScriptedPanelClientFactory panels)
    {
        var factory = new PanelFleetWebApplicationFactory
        {
            AdditionalServices = services =>
            {
                services.RemoveAll<IPanelClientFactory>();
                services.AddSingleton<IPanelClientFactory>(panels);
                services.AddPanelUpdateModule(options =>
                {
                    options.CheckTimeoutSeconds = 5;
                    options.HealthCheckTimeoutSeconds = 10;
                    options.HealthCheckIntervalSeconds = 1;
                });
            }
        };

        return factory;
    }

    private static ActorInfo PanelUpdateTestActor() =>
        new("integration-user", "Integration User", "it@example.test", "127.0.0.1", "trace-p12");

    private static async Task<Guid> CreateNodeAsync(
        HttpClient client,
        string token,
        string cookie,
        string name)
    {
        var request = CreateJsonRequest(
            HttpMethod.Post,
            "/api/nodes",
            new
            {
                name,
                baseUrl = $"https://{name}.example.test",
                apiKey = "test-api-key-1234",
                authMode = "Auto",
                tlsPolicy = "Strict",
                isEnabled = true,
                tags = Array.Empty<string>()
            },
            token,
            cookie);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        object? body,
        string token,
        string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static async Task<(string Token, string Cookie)> GetCsrfAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/antiforgery/token");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("缺少防伪令牌。");

        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith("PanelFleet.Antiforgery=", StringComparison.Ordinal))
            : null;

        var cookie = setCookie?.Split(';')[0]
            ?? throw new InvalidOperationException("缺少防伪 Cookie。");

        return (token, cookie);
    }

    /// <summary>按节点名脚本化的假 1Panel 客户端工厂（只用于集成测试，不访问真实节点）。</summary>
    private sealed class ScriptedPanelClientFactory : IPanelClientFactory
    {
        private readonly ConcurrentDictionary<string, FakePanelScript> _scripts =
            new(StringComparer.OrdinalIgnoreCase);

        public FakePanelScript Script(string nodeName) =>
            _scripts.GetOrAdd(nodeName, name => new FakePanelScript(name));

        public IPanelClient Create(NodeDescriptor node) => new FakePanelClient(Script(node.Name));

        private sealed class FakePanelClient : IPanelClient
        {
            private readonly FakePanelScript _script;
            private int _submitted;

            public FakePanelClient(FakePanelScript script)
            {
                _script = script;
                Node = new NodeDescriptor(
                    NodeId.New(),
                    script.Name,
                    new Uri("http://127.0.0.1:1000"),
                    "fake-api-key",
                    AuthMode.HmacSha256,
                    TlsPolicy.Strict);
            }

            public NodeDescriptor Node { get; }

            public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

            public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default)
            {
                if (relativePath == UpdateRoutes.UpgradeInfo && typeof(T) == typeof(PanelUpgradeInfoDto))
                {
                    var dto = new PanelUpgradeInfoDto
                    {
                        LatestVersion = _script.LatestVersion,
                        NewVersion = string.Empty,
                        ReleaseNote = "## 集成测试更新说明"
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
                    var version = Volatile.Read(ref _submitted) == 0
                        ? _script.CurrentVersion
                        : _script.VersionAfterUpgrade ?? _script.CurrentVersion;
                    var dto = new PanelSettingBaseDto { SystemVersion = version };
                    return Task.FromResult(new PanelCallResult<T>((T)(object)dto, null, TimeSpan.Zero));
                }

                if (relativePath == UpdateRoutes.UpgradeExecute)
                {
                    var version = ExtractVersion(body);
                    _script.SubmittedVersions.Add(version);
                    if (!_script.SubmitSucceeds)
                    {
                        return Task.FromResult(Failure<T>(PanelErrorKind.PanelServerError, "下载升级包失败"));
                    }

                    Interlocked.Increment(ref _submitted);
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
                var reachable = Volatile.Read(ref _submitted) == 0
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
                                Free = 10L * 1024 * 1024 * 1024
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

    /// <summary>单个节点的假面板脚本。</summary>
    private sealed class FakePanelScript
    {
        public FakePanelScript(string name) => Name = name;

        public string Name { get; }

        public string CurrentVersion { get; set; } = "v2.3.2";

        public string? LatestVersion { get; set; } = "v2.5.1";

        public bool DashboardReachable { get; set; } = true;

        public bool DashboardReachableAfterUpgrade { get; set; } = true;

        public bool SubmitSucceeds { get; set; } = true;

        public string? VersionAfterUpgrade { get; set; }

        public List<string> SubmittedVersions { get; } = [];

        public int HealthCalls { get; set; }
    }
}
