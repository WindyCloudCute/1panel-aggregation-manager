using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PanelFleet.Client.Dto;
using PanelFleet.Client.Errors;
using PanelFleet.Client.Handlers;
using PanelFleet.Client.Serialization;
using PanelFleet.Client.Signing;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>单个 1Panel 节点的 HTTP 客户端实现。</summary>
internal sealed class PanelClient : IPanelClient
{
    // 小于该阈值的偏差属于 HTTP Date 头秒级截断/刷新误差，不值得重试；
    // 只有明显的时间不同步（如 NTP 偏差）才触发校准重试。
    private static readonly TimeSpan ClockSkewThreshold = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly PanelHttpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _authLock = new();

    private AuthMode _authMode;
    private TimeSpan _clockOffset;
    private bool _authResolved;

    public PanelClient(
        NodeDescriptor node,
        HttpClient httpClient,
        PanelHttpOptions options,
        TimeProvider timeProvider,
        ILogger<PanelClient> logger)
    {
        Node = node;
        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _authMode = node.AuthMode == AuthMode.Auto ? AuthMode.HmacSha256 : node.AuthMode;
        _clockOffset = node.ClockOffset;
    }

    public NodeDescriptor Node { get; }

    public PanelAuthState AuthState
    {
        get
        {
            lock (_authLock)
            {
                return new PanelAuthState(_authMode, _clockOffset, _authResolved);
            }
        }
    }

    public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync<OsInfoDto>(PanelRoutes.DashboardOs, cancellationToken);

    public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync<DashboardCurrentDto>(PanelRoutes.DashboardCurrentNode, cancellationToken);

    public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
        string ioOption = "all",
        string netOption = "all",
        CancellationToken cancellationToken = default) =>
        GetAsync<DashboardBaseDto>(PanelRoutes.DashboardBase(ioOption, netOption), cancellationToken);

    public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, relativePath, body: null, cancellationToken);

    public Task<PanelCallResult<T>> PostAsync<T>(string relativePath, object? body, CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Post, relativePath, body, cancellationToken);

    public async Task<PanelCallResult<T>> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        Uri uri;
        try
        {
            uri = PanelEndpoint.Combine(Node.BaseUri, relativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return Failure<T>(
                PanelErrorMapper.Create(PanelErrorKind.Protocol, Node.Name, detail: exception.Message, cause: exception),
                stopwatch.Elapsed);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        try
        {
            using var response = await SendWithAuthAsync(method, uri, body, timeoutCts.Token).ConfigureAwait(false);
            return await ReadAsync<T>(response, stopwatch.Elapsed, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var error = PanelErrorMapper.Create(
                PanelErrorKind.Timeout,
                Node.Name,
                detail: $"超过 {_options.RequestTimeout.TotalSeconds:0.#} 秒");
            _logger.LogWarning("节点 {NodeId} 请求超时: {Kind} {Path}", Node.Id, error.Kind, relativePath);
            return Failure<T>(error, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return Failure<T>(
                PanelErrorMapper.Create(PanelErrorKind.Canceled, Node.Name),
                stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            var error = PanelErrorMapper.MapTransport(exception, Node.Name);
            _logger.LogWarning("节点 {NodeId} 网络/TLS 错误: {Kind}", Node.Id, error.Kind);
            return Failure<T>(error, stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var error = PanelErrorMapper.Create(
                PanelErrorKind.Protocol,
                Node.Name,
                detail: exception.Message,
                cause: exception);
            _logger.LogWarning("节点 {NodeId} 协议错误: {Kind}", Node.Id, error.Kind);
            return Failure<T>(error, stopwatch.Elapsed);
        }
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(
        HttpMethod method,
        Uri uri,
        object? body,
        CancellationToken cancellationToken)
    {
        var (mode, offset) = SnapshotAuth();

        var response = await SendRawAsync(method, uri, body, mode, offset, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            RememberAuth(mode, offset);
            return response;
        }

        var effectiveOffset = offset;

        if (response.Headers.Date is { } serverDate)
        {
            var skew = serverDate - _timeProvider.GetUtcNow();
            if (skew.Duration() > ClockSkewThreshold)
            {
                _logger.LogInformation(
                    "节点 {NodeId} 检测到时间偏差 {SkewSeconds:0.#} 秒，使用校准时间戳重试",
                    Node.Id,
                    skew.TotalSeconds);
                effectiveOffset = offset + skew;
                response.Dispose();
                response = await SendRawAsync(method, uri, body, mode, effectiveOffset, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    RememberAuth(mode, effectiveOffset);
                    return response;
                }
            }
        }

        if (Node.AuthMode == AuthMode.Auto && mode != AuthMode.Md5)
        {
            _logger.LogInformation("节点 {NodeId} HMAC 鉴权失败，回退 MD5", Node.Id);
            response.Dispose();
            response = await SendRawAsync(method, uri, body, AuthMode.Md5, effectiveOffset, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                RememberAuth(AuthMode.Md5, effectiveOffset);
                return response;
            }
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        Uri uri,
        object? body,
        AuthMode mode,
        TimeSpan clockOffset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        ApiKeySigningHandler.Attach(
            request,
            new SigningContext(Node.ApiKey, mode, clockOffset, _timeProvider));

        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "节点 {NodeId} 鉴权 {Mode} 响应 HTTP {Status}",
            Node.Id,
            mode,
            (int)response.StatusCode);

        return response;
    }

    private async Task<PanelCallResult<T>> ReadAsync<T>(
        HttpResponseMessage response,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return Failure<T>(
                PanelErrorMapper.FromHttpStatus(HttpStatusCode.Unauthorized, TryExtractMessage(body), null, Node.Name),
                elapsed);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Failure<T>(
                PanelErrorMapper.FromHttpStatus(response.StatusCode, TryExtractMessage(body), null, Node.Name),
                elapsed);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new PanelCallResult<T>(default, null, elapsed);
        }

        if (LooksLikeHtml(body))
        {
            return Failure<T>(
                PanelErrorMapper.Create(
                    PanelErrorKind.Protocol,
                    Node.Name,
                    detail: "响应是 HTML 页面（可能是 1Panel 安全入口或反向代理登录页）；节点地址应填写 API 根地址，例如 http://host:port，不要包含 /myentrance 之类的安全入口。"),
                elapsed);
        }

        PanelEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PanelEnvelope<T>>(body, PanelJson.Options);
        }
        catch (JsonException exception)
        {
            return Failure<T>(
                PanelErrorMapper.Create(
                    PanelErrorKind.Protocol,
                    Node.Name,
                    detail: $"响应不是合法 JSON：{exception.Message}",
                    cause: exception),
                elapsed);
        }

        if (envelope is null)
        {
            return Failure<T>(
                PanelErrorMapper.Create(PanelErrorKind.Protocol, Node.Name, detail: "响应信封为空"),
                elapsed);
        }

        if (envelope.Code == PanelErrorMapper.PanelSuccessCode)
        {
            return new PanelCallResult<T>(envelope.Data, null, elapsed);
        }

        return Failure<T>(
            PanelErrorMapper.FromPanelCode(envelope.Code, envelope.Message, envelope.ErrorCode, Node.Name),
            elapsed);
    }

    private static bool LooksLikeHtml(string body)
    {
        var trimmed = body.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '<';
    }

    private static string? TryExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private (AuthMode Mode, TimeSpan Offset) SnapshotAuth()
    {
        lock (_authLock)
        {
            return (_authMode, _clockOffset);
        }
    }

    private void RememberAuth(AuthMode mode, TimeSpan offset)
    {
        lock (_authLock)
        {
            _authMode = mode;
            _clockOffset = offset;
            _authResolved = true;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static PanelCallResult<T> Failure<T>(PanelError error, TimeSpan elapsed) =>
        new(default, error, elapsed);
}
