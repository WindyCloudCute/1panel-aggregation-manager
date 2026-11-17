using System.Threading.Channels;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Application.Terminal;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Terminal;

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
        Task.FromResult(Nodes.Any(node => node.Name == name && node.Id != exceptId));

    public Task<bool> BaseUrlExistsAsync(Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken) =>
        Task.FromResult(Nodes.Any(node => node.BaseUrl == baseUrl && node.Id != exceptId));

    public Task AddAsync(NodeRecord node, CancellationToken cancellationToken)
    {
        Nodes.Add(node);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(NodeRecord node, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(NodeId id, CancellationToken cancellationToken) => Task.CompletedTask;

    public static NodeRecord CreateNode(string name = "node-a", bool enabled = true, NodeId? id = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new NodeRecord(
            id ?? NodeId.New(),
            name,
            new Uri("http://198.51.100.10:1000"),
            null,
            "protected-key",
            "hint",
            AuthMode.Auto,
            AuthMode.Md5,
            0,
            TlsPolicy.Strict,
            null,
            [],
            null,
            enabled,
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

internal sealed class FakeSecretProtector : INodeSecretProtector
{
    public const string ApiKey = "unit-test-api-key";

    public ProtectedSecret Protect(string apiKey) => new("protected", "hint");

    public string Unprotect(string cipherText) => ApiKey;
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
            return Task.FromResult<IReadOnlyList<AuditEntry>>(
                Entries.Where(entry => action is null || entry.Action == action).Take(take).ToArray());
        }
    }
}

internal sealed class FakePanelFailure
{
    public FakePanelFailure(PanelError error) => Error = error;

    public PanelError Error { get; }
}

internal sealed class FakePanelClient : IPanelClient
{
    private readonly Dictionary<string, Func<object?, object?>> _handlers = new(StringComparer.Ordinal);

    public FakePanelClient(NodeDescriptor node)
    {
        Node = node;
        AuthState = new PanelAuthState(AuthMode.Md5, TimeSpan.Zero, true);
    }

    public NodeDescriptor Node { get; }

    public PanelAuthState AuthState { get; set; }

    public List<string> Calls { get; } = [];

    public FakePanelClient OnGet<T>(string path, T data)
    {
        _handlers[path] = _ => data;
        return this;
    }

    public FakePanelClient OnPost<T>(string path, T data)
    {
        _handlers[path] = _ => data;
        return this;
    }

    public FakePanelClient OnGetError(string path, PanelErrorKind kind = PanelErrorKind.Network)
    {
        _handlers[path] = _ => new FakePanelFailure(
            new PanelError(kind, "fake panel error"));
        return this;
    }

    public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Build<T>(relativePath, null));

    public Task<PanelCallResult<T>> PostAsync<T>(string relativePath, object? body, CancellationToken cancellationToken = default) =>
        Task.FromResult(Build<T>(relativePath, body));

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

    private PanelCallResult<T> Build<T>(string path, object? body)
    {
        Calls.Add(path);
        if (!_handlers.TryGetValue(path, out var handler))
        {
            return new PanelCallResult<T>(default, null, TimeSpan.Zero);
        }

        var value = handler(body);
        if (value is FakePanelFailure failure)
        {
            return new PanelCallResult<T>(default, failure.Error, TimeSpan.Zero);
        }

        return new PanelCallResult<T>(value is null ? default : (T)value, null, TimeSpan.Zero);
    }
}

internal sealed class FakePanelClientFactory : IPanelClientFactory
{
    public FakePanelClient Client { get; set; } = new(new NodeDescriptor(
        NodeId.New(),
        "fake",
        new Uri("http://127.0.0.1:1000"),
        FakeSecretProtector.ApiKey));

    public IPanelClient Create(NodeDescriptor node) => Client;
}

internal sealed class FakeTerminalBackend : ITerminalBackend
{
    private readonly Queue<Func<TerminalConnectContext, string?, bool, ITerminalConnection>> _connections = new();

    public List<(string? SessionId, bool Revalidate)> Calls { get; } = [];

    public TerminalException? Failure { get; set; }

    public FakeTerminalBackend Enqueue(FakeTerminalConnection connection)
    {
        _connections.Enqueue((_, _, _) => connection);
        return this;
    }

    public FakeTerminalBackend EnqueueFactory(Func<string?, bool, ITerminalConnection> factory)
    {
        _connections.Enqueue((_, sessionId, revalidate) => factory(sessionId, revalidate));
        return this;
    }

    public Task<ITerminalConnection> ConnectAsync(
        TerminalConnectContext context,
        string? sessionId,
        bool revalidate,
        CancellationToken cancellationToken)
    {
        Calls.Add((sessionId, revalidate));
        if (Failure is not null)
        {
            throw Failure;
        }

        if (_connections.Count == 0)
        {
            throw new InvalidOperationException("FakeTerminalBackend 没有可用的连接。");
        }

        return Task.FromResult(_connections.Dequeue()(context, sessionId, revalidate));
    }
}

internal sealed class FakeTerminalConnection : ITerminalConnection
{
    private readonly Channel<TerminalServerFrame> _frames = Channel.CreateUnbounded<TerminalServerFrame>();

    public FakeTerminalConnection(string? sessionId = "1panel-session-1")
    {
        SessionId = sessionId;
    }

    public string? SessionId { get; set; }

    public List<byte[]> Inputs { get; } = [];

    public List<(int Cols, int Rows)> Resizes { get; } = [];

    public bool Closed { get; private set; }

    public bool Disposed { get; private set; }

    public void Push(TerminalServerFrame frame) => _frames.Writer.TryWrite(frame);

    public void PushOutput(string text) =>
        Push(new TerminalServerFrame(TerminalServerFrameKind.Output, Data: System.Text.Encoding.UTF8.GetBytes(text)));

    public void PushSession(string? id = null) =>
        Push(new TerminalServerFrame(TerminalServerFrameKind.Session, SessionId: id ?? SessionId));

    public void PushClosed(int? code, string? reason = null) =>
        Push(new TerminalServerFrame(TerminalServerFrameKind.Closed, CloseCode: code, CloseReason: reason));

    public async Task<TerminalServerFrame> ReceiveAsync(CancellationToken cancellationToken) =>
        await _frames.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public Task SendInputAsync(byte[] data, CancellationToken cancellationToken)
    {
        lock (Inputs)
        {
            Inputs.Add(data);
        }

        return Task.CompletedTask;
    }

    public Task SendResizeAsync(int cols, int rows, CancellationToken cancellationToken)
    {
        lock (Resizes)
        {
            Resizes.Add((cols, rows));
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        _frames.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeTerminalChannel : ITerminalChannel
{
    private readonly Channel<TerminalChannelMessage> _incoming = Channel.CreateUnbounded<TerminalChannelMessage>();

    public List<string> Sent { get; } = [];

    public bool Closed { get; private set; }

    public int CloseCode { get; private set; }

    public void PushText(string payload) =>
        _incoming.Writer.TryWrite(new TerminalChannelMessage(TerminalChannelMessageKind.Text, payload));

    public void PushInput(byte[] data) =>
        PushText("{\"type\":\"input\",\"data\":\"" + Convert.ToBase64String(data) + "\"}");

    public void PushResize(int cols, int rows) =>
        PushText("{\"type\":\"resize\",\"cols\":" + cols.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"rows\":" + rows.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

    public void PushPing(long timestamp) =>
        PushText("{\"type\":\"ping\",\"timestamp\":"
            + timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "}");

    public void PushClosed()
    {
        _incoming.Writer.TryWrite(new TerminalChannelMessage(TerminalChannelMessageKind.Closed));
        _incoming.Writer.TryComplete();
    }

    public async Task<TerminalChannelMessage> ReceiveAsync(CancellationToken cancellationToken) =>
        await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public Task SendTextAsync(string payload, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add(payload);
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(int statusCode, string reason, CancellationToken cancellationToken)
    {
        Closed = true;
        CloseCode = statusCode;
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> MessagesOfType(string type)
    {
        lock (Sent)
        {
            return Sent.Where(payload => payload.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal)).ToArray();
        }
    }

    public static byte[] DecodeOutput(string payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data").GetString();
        return Convert.FromBase64String(data ?? string.Empty);
    }
}

internal static class TerminalTestHelpers
{
    public static TerminalSessionInfo CreateInfo(
        TerminalMode mode = TerminalMode.Host,
        string? container = null,
        NodeId? nodeId = null)
    {
        return new TerminalSessionInfo(
            "session-1",
            nodeId ?? NodeId.New(),
            "node-a",
            mode,
            container,
            DateTimeOffset.UtcNow,
            100,
            30);
    }

    public static TerminalConnectContext CreateContext(
        TerminalMode mode = TerminalMode.Host,
        string? container = null,
        NodeId? nodeId = null)
    {
        var id = nodeId ?? NodeId.New();
        return new TerminalConnectContext(
            new NodeDescriptor(id, "node-a", new Uri("http://198.51.100.10:1000"), FakeSecretProtector.ApiKey),
            mode,
            container,
            mode == TerminalMode.Container ? "/bin/sh" : null,
            mode == TerminalMode.Container ? string.Empty : null,
            100,
            30);
    }

    public static TerminalLimits FastLimits(
        TimeSpan? idle = null,
        TimeSpan? maxDuration = null,
        int maxFrameBytes = 65536,
        int maxFramesPerSecond = 1000,
        int maxBytesPerSecond = 10 * 1024 * 1024)
    {
        return new TerminalLimits(
            idle ?? TimeSpan.FromMinutes(30),
            maxDuration ?? TimeSpan.FromMinutes(240),
            maxFrameBytes,
            maxFramesPerSecond,
            maxBytesPerSecond,
            RevalidationDelayMs: 5);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("等待条件超时。");
    }

    public static async Task<T> WithTimeoutAsync<T>(Task<T> task, int timeoutMs = 5000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException("等待任务超时。");
        }

        return await task.ConfigureAwait(false);
    }
}
