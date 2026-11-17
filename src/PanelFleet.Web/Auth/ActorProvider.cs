using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using PanelFleet.Application.Models;

namespace PanelFleet.Web.Auth;

public interface IActorProvider
{
    Task<ActorInfo> GetAsync();
}

public sealed class ActorProvider : IActorProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public ActorProvider(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationStateProvider authenticationStateProvider)
    {
        _httpContextAccessor = httpContextAccessor;
        _authenticationStateProvider = authenticationStateProvider;
    }

    public async Task<ActorInfo> GetAsync()
    {
        var principal = _httpContextAccessor.HttpContext?.User;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            principal = state.User;
        }

        return ActorFactory.FromPrincipal(principal, _httpContextAccessor.HttpContext);
    }
}

public static class ActorFactory
{
    public static ActorInfo FromPrincipal(ClaimsPrincipal? principal, HttpContext? httpContext)
    {
        var identity = principal?.Identity;
        if (identity?.IsAuthenticated != true)
        {
            return new ActorInfo(null, null, null, ClientIp(httpContext), TraceId(httpContext));
        }

        return new ActorInfo(
            Find(principal!, "sub", ClaimTypes.NameIdentifier),
            Find(principal!, "name", ClaimTypes.Name),
            Find(principal!, "email", ClaimTypes.Email),
            ClientIp(httpContext),
            TraceId(httpContext));
    }

    private static string? Find(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ClientIp(HttpContext? httpContext) =>
        httpContext?.Connection.RemoteIpAddress?.ToString();

    private static string? TraceId(HttpContext? httpContext) =>
        Activity.Current?.Id ?? httpContext?.TraceIdentifier;
}
