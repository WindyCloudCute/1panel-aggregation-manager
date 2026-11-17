using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>创建节点客户端；默认使用 <see cref="SocketsHttpHandler"/>，测试可注入主处理器。</summary>
public sealed class PanelClientFactory : IPanelClientFactory
{
    private readonly PanelHttpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<NodeDescriptor, HttpMessageHandler>? _primaryHandlerFactory;

    public PanelClientFactory(
        PanelHttpOptions? options = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        Func<NodeDescriptor, HttpMessageHandler>? primaryHandlerFactory = null)
    {
        _options = options ?? new PanelHttpOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _primaryHandlerFactory = primaryHandlerFactory;
        ValidateOptions(_options);
    }

    public IPanelClient Create(NodeDescriptor node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var primary = _primaryHandlerFactory?.Invoke(node) ?? CreatePrimaryHandler(node);
        var signing = new Handlers.ApiKeySigningHandler(primary);

        var httpClient = new HttpClient(signing, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PanelFleet/0.1");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        return new PanelClient(
            node,
            httpClient,
            _options,
            _timeProvider,
            _loggerFactory.CreateLogger<PanelClient>());
    }

    private SocketsHttpHandler CreatePrimaryHandler(NodeDescriptor node)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
            ConnectTimeout = _options.ConnectTimeout,
            PooledConnectionLifetime = _options.PooledConnectionLifetime,
            MaxConnectionsPerServer = _options.MaxConnectionsPerServer
        };

        if (node.TlsPolicy == TlsPolicy.AllowSelfSigned)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }

    private static void ValidateOptions(PanelHttpOptions options)
    {
        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout 必须大于 0。");
        }

        if (options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectTimeout 必须大于 0。");
        }

        if (options.MaxConnectionsPerServer <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConnectionsPerServer 必须大于 0。");
        }
    }
}
