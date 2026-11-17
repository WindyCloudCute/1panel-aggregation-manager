using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Client.Errors;
using PanelFleet.Client.Handlers;
using PanelFleet.Client.Signing;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>容器/Compose 日志一次性 tail 查询参数。</summary>
public sealed record ContainerLogQuery(
    string? Container,
    string? Compose,
    string Since = "all",
    int Tail = 100,
    bool Timestamp = false);

/// <summary>日志 tail 读取结果（只读，不跟随流）。</summary>
public sealed record ContainerLogReadResult(
    IReadOnlyList<string> Lines,
    bool Truncated,
    string? ErrorMessage,
    PanelError? Error,
    TimeSpan Elapsed);

/// <summary>
/// 容器日志域客户端：读取 1Panel 的 SSE 日志流（<c>GET /api/v2/containers/search/log</c>）。
/// <para>IPanelClient 只处理 JSON 信封，无法读取 text/event-stream；日志单独走本域客户端。</para>
/// </summary>
public interface IContainerLogClient : IDisposable
{
    Task<ContainerLogReadResult> ReadAsync(ContainerLogQuery query, CancellationToken cancellationToken);
}

/// <summary>按节点创建容器日志客户端。</summary>
public interface IContainerLogClientFactory
{
    IContainerLogClient Create(NodeDescriptor node);
}

/// <summary>
/// 容器日志客户端工厂：复用程序集内的 <see cref="ApiKeySigningHandler"/> 完成 1Panel 签名，
/// 并遵循节点的 TLS 策略。实现不记录 API Key。
/// </summary>
public sealed class ContainerLogClientFactory : IContainerLogClientFactory
{
    private readonly PanelHttpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public ContainerLogClientFactory(
        PanelHttpOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IContainerLogClient Create(NodeDescriptor node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var primary = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = _options.ConnectTimeout,
            PooledConnectionLifetime = _options.PooledConnectionLifetime,
            MaxConnectionsPerServer = _options.MaxConnectionsPerServer
        };

        if (node.TlsPolicy == TlsPolicy.AllowSelfSigned)
        {
            primary.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        var httpClient = new HttpClient(new ApiKeySigningHandler(primary), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PanelFleet/0.1");

        return new HttpContainerLogClient(
            node,
            httpClient,
            _timeProvider,
            _loggerFactory.CreateLogger<HttpContainerLogClient>());
    }
}

/// <summary>
/// 基于 SSE 的容器日志读取实现。
/// <para>安全约束：单次最多读取 <see cref="MaxLines"/> 行 / <see cref="MaxChars"/> 字符；</para>
/// <para>默认 follow=false，docker logs 输出完毕后 1Panel 关闭流，因此不会长期占用连接。</para>
/// </summary>
internal sealed class HttpContainerLogClient : IContainerLogClient
{
    internal const int MaxLines = 2000;

    internal const int MaxChars = 1024 * 1024;

    internal const int MaxLineLength = 8192;

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ClockSkewThreshold = TimeSpan.FromSeconds(5);

    private readonly NodeDescriptor _node;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public HttpContainerLogClient(
        NodeDescriptor node,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _node = node;
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ContainerLogReadResult> ReadAsync(
        ContainerLogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Container) && string.IsNullOrWhiteSpace(query.Compose))
        {
            return Failure(
                PanelErrorMapper.Create(PanelErrorKind.Validation, _node.Name, detail: "缺少容器名或 Compose 项目名。"),
                TimeSpan.Zero,
                "缺少容器名或 Compose 项目名。");
        }

        var tail = Math.Clamp(query.Tail, 1, MaxLines);
        var relativePath = ContainerRoutes.ContainerLog(
            query.Container,
            query.Compose,
            query.Since,
            tail,
            follow: false,
            query.Timestamp);

        Uri uri;
        try
        {
            uri = PanelEndpoint.Combine(_node.BaseUri, relativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return Failure(
                PanelErrorMapper.Create(PanelErrorKind.Protocol, _node.Name, detail: exception.Message, cause: exception),
                TimeSpan.Zero,
                exception.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ReadBudget);

        try
        {
            using var response = await SendWithAuthAsync(uri, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, timeoutCts.Token).ConfigureAwait(false);
                var error = PanelErrorMapper.FromHttpStatus(
                    response.StatusCode,
                    TryExtractMessage(body),
                    null,
                    _node.Name);
                return Failure(error, stopwatch.Elapsed, error.Message);
            }

            return await ReadStreamAsync(response, stopwatch, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var error = PanelErrorMapper.Create(PanelErrorKind.Canceled, _node.Name);
            return Failure(error, stopwatch.Elapsed, "已取消。");
        }
        catch (OperationCanceledException)
        {
            var error = PanelErrorMapper.Create(
                PanelErrorKind.Timeout,
                _node.Name,
                detail: $"日志读取超过 {ReadBudget.TotalSeconds:0} 秒");
            _logger.LogWarning("容器日志读取超时 节点 {NodeId} 容器 {Container}", _node.Id, query.Container);
            return Failure(error, stopwatch.Elapsed, error.Message);
        }
        catch (HttpRequestException exception)
        {
            var error = PanelErrorMapper.MapTransport(exception, _node.Name);
            return Failure(error, stopwatch.Elapsed, error.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var error = PanelErrorMapper.Create(
                PanelErrorKind.Protocol,
                _node.Name,
                detail: exception.Message,
                cause: exception);
            _logger.LogWarning("容器日志读取异常 节点 {NodeId} 类型 {Type}", _node.Id, exception.GetType().Name);
            return Failure(error, stopwatch.Elapsed, error.Message);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<HttpResponseMessage> SendWithAuthAsync(Uri uri, CancellationToken cancellationToken)
    {
        var mode = _node.AuthMode == AuthMode.Auto ? AuthMode.HmacSha256 : _node.AuthMode;
        var offset = _node.ClockOffset;

        var response = await SendRawAsync(uri, mode, offset, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var effectiveOffset = offset;
        if (response.Headers.Date is { } serverDate)
        {
            var skew = serverDate - _timeProvider.GetUtcNow();
            if (skew.Duration() > ClockSkewThreshold)
            {
                effectiveOffset = offset + skew;
                response.Dispose();
                response = await SendRawAsync(uri, mode, effectiveOffset, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    return response;
                }
            }
        }

        if (_node.AuthMode == AuthMode.Auto && mode != AuthMode.Md5)
        {
            response.Dispose();
            response = await SendRawAsync(uri, AuthMode.Md5, effectiveOffset, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        Uri uri,
        AuthMode mode,
        TimeSpan clockOffset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        ApiKeySigningHandler.Attach(
            request,
            new SigningContext(_node.ApiKey, mode, clockOffset, _timeProvider));

        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ContainerLogReadResult> ReadStreamAsync(
        HttpResponseMessage response,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>(Math.Min(MaxLines, 128));
        var truncated = false;
        var totalChars = 0;
        var sawData = false;
        string? pendingEvent = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                pendingEvent = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                pendingEvent = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            sawData = true;
            var payload = line[5..];
            if (payload.StartsWith(' '))
            {
                payload = payload[1..];
            }

            if (string.Equals(pendingEvent, "error", StringComparison.OrdinalIgnoreCase))
            {
                var error = PanelErrorMapper.Create(
                    PanelErrorKind.PanelServerError,
                    _node.Name,
                    detail: Trim(payload, 800));
                return new ContainerLogReadResult(lines, truncated, payload, error, stopwatch.Elapsed);
            }

            if (lines.Count >= MaxLines || totalChars >= MaxChars)
            {
                truncated = true;
                break;
            }

            if (payload.Length > MaxLineLength)
            {
                payload = payload[..MaxLineLength] + "…";
                truncated = true;
            }

            totalChars += payload.Length;
            lines.Add(payload);
        }

        if (!sawData && lines.Count == 0)
        {
            // 1Panel 在 Accept 不是 text/event-stream 时会返回 JSON 信封；这里明确提示协议不匹配。
            var error = PanelErrorMapper.Create(
                PanelErrorKind.Protocol,
                _node.Name,
                detail: "1Panel 未返回 SSE 日志流（检查 Accept: text/event-stream 与节点版本）。");
            return new ContainerLogReadResult(lines, truncated, error.Message, error, stopwatch.Elapsed);
        }

        return new ContainerLogReadResult(lines, truncated, null, null, stopwatch.Elapsed);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Trim(body, 2000);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    private static string? TryExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == System.Text.Json.JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static ContainerLogReadResult Failure(PanelError error, TimeSpan elapsed, string message) =>
        new([], false, message, error, elapsed);

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}
