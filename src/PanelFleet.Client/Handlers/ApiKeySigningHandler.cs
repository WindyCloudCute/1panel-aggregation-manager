using System.Globalization;
using PanelFleet.Client.Signing;

namespace PanelFleet.Client.Handlers;

/// <summary>为每个请求写入 1Panel-Timestamp / 1Panel-Token 请求头。</summary>
internal sealed class ApiKeySigningHandler : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<SigningContext> ContextKey = new("PanelFleet.SigningContext");

    public ApiKeySigningHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    public static void Attach(HttpRequestMessage request, SigningContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        request.Options.Set(ContextKey, context);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(ContextKey, out var context))
        {
            throw new InvalidOperationException("请求缺少 1Panel 签名上下文。");
        }

        var timestamp = context.TimeProvider
            .GetUtcNow()
            .Add(context.ClockOffset)
            .ToUnixTimeSeconds();

        var token = ApiKeySigner.ComputeToken(context.ApiKey, timestamp, context.Mode);

        request.Headers.TryAddWithoutValidation("1Panel-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("1Panel-Token", token);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
