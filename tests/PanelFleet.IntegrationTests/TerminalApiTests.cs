// 说明：终端端点测试使用「自建最小 TestServer 宿主」而不是 PanelFleetWebApplicationFactory，
// 以便本阶段测试不依赖 Program 接线（AddTerminalModule/MapTerminalEndpoints 由 Lead 统一接线）。
// Classic WebHostBuilder / TestServer(IWebHostBuilder) 在 .NET 10 标记为过时（ASPDEPR004/ASPDEPR008），
// 但这是 TestServer 现有可用的宿主构造方式；这里仅限本测试文件局部禁用这两个诊断。
#pragma warning disable ASPDEPR004, ASPDEPR008

using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Terminal;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;
using PanelFleet.Web.Auth;
using PanelFleet.Web.Terminal;

namespace PanelFleet.IntegrationTests;

/// <summary>
/// 终端端点集成测试：自建最小 TestServer（不依赖真实 1Panel，也不依赖 Program 接线），
/// 覆盖鉴权/开关/Origin/参数/WS 成功路径/会话生命周期与审计。
/// </summary>
public sealed class TerminalApiTests
{
    private const string AdminUser = "admin";
    private const string ViewerUser = "viewer";

    [Fact]
    public async Task Terminal_WithoutAuthentication_Returns401()
    {
        using var host = CreateHost();
        using var client = host.CreateClient();

        var response = await client.GetAsync("/ws/terminal?nodeId=" + Guid.NewGuid().ToString("D"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_NonAdmin_Returns403()
    {
        using var host = CreateHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", ViewerUser);

        var response = await client.GetAsync("/ws/terminal?nodeId=" + Guid.NewGuid().ToString("D"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_WhenDisabled_Returns403WithMessage()
    {
        using var host = CreateHost(services =>
        {
            services.RemoveAll<TerminalOptions>();
            services.AddSingleton(new TerminalOptions { Enabled = false });
        });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", AdminUser);

        var response = await client.GetAsync("/ws/terminal?nodeId=" + Guid.NewGuid().ToString("D"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("终端功能已禁用", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_NonWebSocketRequest_Returns400()
    {
        using var host = CreateHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", AdminUser);

        var response = await client.GetAsync("/ws/terminal?nodeId=" + Guid.NewGuid().ToString("D"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_InvalidNodeId_Returns400()
    {
        using var host = CreateHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", AdminUser);

        var response = await client.GetAsync("/ws/terminal?nodeId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_CrossOrigin_Returns403()
    {
        using var host = CreateHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", AdminUser);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example.com");

        var response = await client.GetAsync("/ws/terminal?nodeId=" + Guid.NewGuid().ToString("D"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_SameOriginNonWebSocketRequest_PassesOriginCheck()
    {
        using var host = CreateHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", AdminUser);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        var response = await client.GetAsync("/ws/terminal?nodeId=" + Guid.NewGuid().ToString("D"));

        // 通过 Origin 校验后因「不是 WebSocket 请求」返回 400，而不是 403。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_WebSocket_ReceivesReadyOutputAndWritesAudit()
    {
        var fixtures = new TerminalHostFixtures();
        using var host = CreateHost(services => fixtures.Register(services));
        var node = fixtures.Node;

        using var socket = await ConnectAsync(host, node.Id);
        using var ready = await ReceiveJsonAsync(socket);
        Assert.Equal("ready", ready.RootElement.GetProperty("type").GetString());
        Assert.Equal("host", ready.RootElement.GetProperty("mode").GetString());

        fixtures.Connection.PushOutput("hello from node");
        using var output = await ReceiveJsonAsync(socket);
        Assert.Equal("output", output.RootElement.GetProperty("type").GetString());
        var bytes = Convert.FromBase64String(output.RootElement.GetProperty("data").GetString()!);
        Assert.Equal("hello from node", Encoding.UTF8.GetString(bytes));

        var input = Encoding.UTF8.GetBytes("echo ok\n");
        await SendAsync(socket, "{\"type\":\"input\",\"data\":\"" + Convert.ToBase64String(input) + "\"}");
        await WaitUntilAsync(() => fixtures.Connection.Inputs.Count == 1);
        Assert.Equal(input, fixtures.Connection.Inputs[0]);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        await WaitUntilAsync(() => fixtures.Audit.Entries.Count >= 2);

        Assert.Equal("terminal.session.start", fixtures.Audit.Entries[0].Action);
        Assert.Equal("terminal.session.end", fixtures.Audit.Entries[1].Action);
        Assert.Equal(node.Id, fixtures.Audit.Entries[0].NodeId);

        using var detail = JsonDocument.Parse(fixtures.Audit.Entries[1].DetailJson!);
        Assert.Equal("user", detail.RootElement.GetProperty("closeReason").GetString());
    }

    [Fact]
    public async Task Terminal_SessionManager_RegistersAndUnregistersSession()
    {
        var fixtures = new TerminalHostFixtures();
        using var host = CreateHost(services => fixtures.Register(services));

        using var scope = host.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ITerminalSessionManager>();
        var registry = scope.ServiceProvider.GetRequiredService<TerminalSessionRegistry>();

        var handle = await manager.OpenAsync(
            new TerminalSessionRequest(fixtures.Node.Id, TerminalMode.Host, null, null, null, 100, 30),
            ActorInfo.System,
            CancellationToken.None);

        Assert.Equal(1, registry.Count);
        Assert.Single(fixtures.Audit.Entries);

        var channel = new InMemoryTerminalChannel();
        channel.PushClosed();
        var outcome = await handle.RunAsync(channel, CancellationToken.None);

        Assert.Equal(TerminalCloseReasons.User, outcome.Reason);
        Assert.Equal(0, registry.Count);
        Assert.Equal(2, fixtures.Audit.Entries.Count);
        Assert.Contains("terminal.session.end", fixtures.Audit.Entries.Select(entry => entry.Action));
    }

    private static async Task<WebSocket> ConnectAsync(TestServer host, NodeId nodeId)
    {
        var client = host.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["X-Test-User"] = AdminUser;
        };

        return await client.ConnectAsync(
            new Uri($"ws://localhost/ws/terminal?nodeId={nodeId.Value:D}&cols=100&rows=30"),
            CancellationToken.None);
    }

    private static TestServer CreateHost(Action<IServiceCollection>? configure = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(TerminalTestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TerminalTestAuthHandler>(
                        TerminalTestAuthHandler.SchemeName,
                        _ => { });
                services.AddAuthorization(options => options.AddPolicy(
                    AuthSetup.AdminPolicy,
                    policy => policy
                        .RequireAuthenticatedUser()
                        .RequireClaim(AuthSetup.AdminRoleClaim, AuthSetup.AdminRoleValue)));
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(new TerminalOptions());
                services.AddSingleton<TerminalAuthCache>();
                services.AddSingleton<TerminalSessionRegistry>();
                services.AddSingleton<ITerminalBackend>(new NullTerminalBackend());
                services.AddScoped<ITerminalSessionManager, TerminalSessionManager>();
                services.AddSingleton<INodeStore>(new TerminalNodeStore());
                services.AddSingleton<INodeSecretProtector>(new TerminalSecretProtector());
                services.AddSingleton<IPanelClientFactory>(new TerminalPanelClientFactory());
                services.AddSingleton<IAuditStore>(new TerminalAuditStore());
                configure?.Invoke(services);
            })
            .Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapTerminalEndpoints());
            });

        return new TestServer(builder);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(WebSocket socket, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new Xunit.Sdk.XunitException("WebSocket 在收到消息前被关闭。");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
    }

    private static Task SendAsync(WebSocket socket, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).AsTask();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("等待条件超时。");
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<TService>(this IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType == typeof(TService))
            {
                services.RemoveAt(index);
            }
        }
    }
}

internal sealed class TerminalTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TerminalTest";

    public TerminalTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var user = values.ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("sub", user),
            new("name", user),
            new("email", user + "@example.test")
        };
        if (string.Equals(user, "admin", StringComparison.Ordinal))
        {
            claims.Add(new Claim(AuthSetup.AdminRoleClaim, AuthSetup.AdminRoleValue));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>测试夹具：节点/审计/假后端。</summary>
internal sealed class TerminalHostFixtures
{
    public TerminalHostFixtures()
    {
        Node = TerminalTestFakes.CreateNode();
        Connection = new TerminalFakeConnection();
        Audit = new TerminalAuditStore();
        Backend = new TestTerminalBackend(Connection);
    }

    public NodeRecord Node { get; }

    public TerminalFakeConnection Connection { get; }

    public TerminalAuditStore Audit { get; }

    public TestTerminalBackend Backend { get; }

    public void Register(IServiceCollection services)
    {
        services.RemoveAll<INodeStore>();
        services.RemoveAll<IAuditStore>();
        services.RemoveAll<ITerminalBackend>();
        services.RemoveAll<IPanelClientFactory>();

        services.AddSingleton<INodeStore>(new TerminalNodeStore(Node));
        services.AddSingleton<IAuditStore>(Audit);
        services.AddSingleton<ITerminalBackend>(Backend);
        services.AddSingleton<IPanelClientFactory>(new TerminalPanelClientFactory());
    }
}

internal static class TerminalTestFakes
{
    public static NodeRecord CreateNode()
    {
        var now = DateTimeOffset.UtcNow;
        return new NodeRecord(
            NodeId.New(),
            "it-node",
            new Uri("http://127.0.0.1:1000"),
            null,
            "protected",
            "hint",
            AuthMode.Auto,
            AuthMode.Md5,
            0,
            TlsPolicy.Strict,
            null,
            [],
            null,
            true,
            "2.3.2",
            0,
            NodeHealth.Online,
            null,
            now,
            now,
            now,
            now);
    }
}

internal sealed class TerminalNodeStore : INodeStore
{
    private readonly NodeRecord _node;

    public TerminalNodeStore()
        : this(TerminalTestFakes.CreateNode())
    {
    }

    public TerminalNodeStore(NodeRecord node) => _node = node;

    public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeRecord>>([_node]);

    public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NodeId>>([_node.Id]);

    public Task<NodeRecord?> FindAsync(NodeId id, CancellationToken cancellationToken) =>
        Task.FromResult<NodeRecord?>(_node.Id == id ? _node : null);

    public Task<bool> NameExistsAsync(string name, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task AddAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class TerminalSecretProtector : INodeSecretProtector
{
    public ProtectedSecret Protect(string apiKey) => new("protected", "hint");

    public string Unprotect(string cipherText) => "it-api-key";
}

internal sealed class TerminalPanelClientFactory : IPanelClientFactory
{
    public IPanelClient Create(NodeDescriptor node) => new TerminalPanelClient(node);
}

internal sealed class TerminalPanelClient : IPanelClient
{
    public TerminalPanelClient(NodeDescriptor node)
    {
        Node = node;
        AuthState = new PanelAuthState(AuthMode.Md5, TimeSpan.Zero, true);
    }

    public NodeDescriptor Node { get; }

    public PanelAuthState AuthState { get; }

    public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default)
    {
        if (relativePath == TerminalRoutes.LocalSshConnection && typeof(T) == typeof(TerminalLocalSshConnectionDto))
        {
            return Task.FromResult(new PanelCallResult<T>(
                (T)(object)new TerminalLocalSshConnectionDto { Addr = "127.0.0.1", Port = 22, User = "root" },
                null,
                TimeSpan.Zero));
        }

        return Task.FromResult(new PanelCallResult<T>(default, null, TimeSpan.Zero));
    }

    public Task<PanelCallResult<T>> PostAsync<T>(string relativePath, object? body, CancellationToken cancellationToken = default)
    {
        if (relativePath == TerminalRoutes.ContainerSearch && typeof(T) == typeof(PagedItemsDto<ContainerItemDto>))
        {
            return Task.FromResult(new PanelCallResult<T>(
                (T)(object)new PagedItemsDto<ContainerItemDto> { Items = [] },
                null,
                TimeSpan.Zero));
        }

        return Task.FromResult(new PanelCallResult<T>(default, null, TimeSpan.Zero));
    }

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

internal sealed class TerminalAuditStore : IAuditStore
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public Task AddAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAsync(
        int take,
        string? action,
        NodeId? nodeId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Entries);
}

internal sealed class NullTerminalBackend : ITerminalBackend
{
    public Task<ITerminalConnection> ConnectAsync(
        TerminalConnectContext context,
        string? sessionId,
        bool revalidate,
        CancellationToken cancellationToken) =>
        throw new TerminalUpstreamException("upstream_unreachable", "测试未配置假终端后端。");
}

internal sealed class TestTerminalBackend : ITerminalBackend
{
    public TestTerminalBackend(params ITerminalConnection[] connections)
    {
        Connections = connections;
    }

    public ITerminalConnection[] Connections { get; }

    public Task<ITerminalConnection> ConnectAsync(
        TerminalConnectContext context,
        string? sessionId,
        bool revalidate,
        CancellationToken cancellationToken) =>
        Task.FromResult(Connections[0]);
}

internal sealed class TerminalFakeConnection : ITerminalConnection
{
    private readonly System.Threading.Channels.Channel<TerminalServerFrame> _frames =
        System.Threading.Channels.Channel.CreateUnbounded<TerminalServerFrame>();

    public string? SessionId { get; set; } = "it-session";

    public List<byte[]> Inputs { get; } = [];

    public bool Closed { get; private set; }

    public void PushOutput(string text) =>
        _frames.Writer.TryWrite(new TerminalServerFrame(
            TerminalServerFrameKind.Output,
            Data: Encoding.UTF8.GetBytes(text)));

    public async Task<TerminalServerFrame> ReceiveAsync(CancellationToken cancellationToken) =>
        await _frames.Reader.ReadAsync(cancellationToken);

    public Task SendInputAsync(byte[] data, CancellationToken cancellationToken)
    {
        lock (Inputs)
        {
            Inputs.Add(data);
        }

        return Task.CompletedTask;
    }

    public Task SendResizeAsync(int cols, int rows, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        _frames.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InMemoryTerminalChannel : ITerminalChannel
{
    private readonly System.Threading.Channels.Channel<TerminalChannelMessage> _incoming =
        System.Threading.Channels.Channel.CreateUnbounded<TerminalChannelMessage>();

    public void PushClosed() => _incoming.Writer.TryWrite(
        new TerminalChannelMessage(TerminalChannelMessageKind.Closed));

    public async Task<TerminalChannelMessage> ReceiveAsync(CancellationToken cancellationToken) =>
        await _incoming.Reader.ReadAsync(cancellationToken);

    public Task SendTextAsync(string payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsync(int statusCode, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}
