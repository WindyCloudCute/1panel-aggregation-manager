using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Auth;
using PanelFleet.Application.Models;

namespace PanelFleet.Web.Auth;

public static class AuthSetup
{
    public const string AdminPolicy = "Admin";
    public const string AdminRoleClaim = "app_role";
    public const string AdminRoleValue = "admin";

    public static IServiceCollection AddPanelFleetAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var oidc = configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();
        oidc.Validate();

        var authorization = BuildAuthorizationOptions(configuration);

        services.AddSingleton(oidc);
        services.AddSingleton(authorization);
        services.AddSingleton<IActorProvider, ActorProvider>();

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "PanelFleet.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/auth/denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        })
        .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.Authority = oidc.Authority;
            options.ClientId = oidc.ClientId;
            options.ClientSecret = oidc.ClientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            options.CallbackPath = oidc.CallbackPath;
            options.SignedOutCallbackPath = oidc.SignedOutCallbackPath;
            options.Scope.Clear();
            foreach (var scope in oidc.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = oidc.RoleClaimType;

            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    if (!string.IsNullOrWhiteSpace(oidc.RedirectUri))
                    {
                        context.ProtocolMessage.RedirectUri = oidc.RedirectUri;
                    }

                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var decision = AuthorizationEvaluator.Evaluate(context.Principal, authorization);

                    if (!decision.Allowed)
                    {
                        context.Fail("not_allowed:" + decision.Reason);
                        return Task.CompletedTask;
                    }

                    if (decision.IsAdmin
                        && context.Principal?.Identity is ClaimsIdentity identity
                        && !identity.HasClaim(AdminRoleClaim, AdminRoleValue))
                    {
                        identity.AddClaim(new Claim(AdminRoleClaim, AdminRoleValue));
                    }

                    return Task.CompletedTask;
                },
                OnTicketReceived = async context =>
                {
                    await TryAuditLoginAsync(context).ConfigureAwait(false);
                },
                OnRemoteFailure = async context =>
                {
                    await TryAuditLoginFailureAsync(context).ConfigureAwait(false);
                    context.Response.Redirect("/auth/denied?reason=login_failed");
                    context.HandleResponse();
                }
            };
        });

        services.AddAuthorization(options =>
            options.AddPolicy(
                AdminPolicy,
                policy => policy.RequireAuthenticatedUser().RequireClaim(AdminRoleClaim, AdminRoleValue)));

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "PanelFleet.Antiforgery";
            options.HeaderName = "X-CSRF-TOKEN";
        });

        return services;
    }

    private static AuthorizationOptions BuildAuthorizationOptions(IConfiguration configuration) => new()
    {
        AllowedEmails = configuration.GetSection("Auth:AllowedEmails").Get<string[]>() ?? [],
        AllowedSubjects = configuration.GetSection("Auth:AllowedSubjects").Get<string[]>() ?? [],
        RoleClaimType = configuration["Oidc:RoleClaimType"] ?? "groups",
        AdminRoles = configuration.GetSection("Oidc:AdminRoles").Get<string[]>() ?? [],
        ReadOnlyRoles = configuration.GetSection("Oidc:ReadOnlyRoles").Get<string[]>() ?? []
    };

    private static async Task TryAuditLoginAsync(TicketReceivedContext context)
    {
        try
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<IAuditStore>();
            var actor = ActorFactory.FromPrincipal(context.Principal, context.HttpContext);
            var entry = new AuditEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                actor,
                "auth.login",
                "user",
                actor.Subject,
                null,
                "{\"provider\":\"pocket-id\"}");

            await store.AddAsync(entry, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 审计失败不阻断登录。
        }
    }

    private static async Task TryAuditLoginFailureAsync(RemoteFailureContext context)
    {
        try
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<IAuditStore>();
            var actor = ActorFactory.FromPrincipal(null, context.HttpContext);
            var entry = new AuditEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                actor,
                "auth.login_failed",
                "user",
                null,
                null,
                "{\"reason\":\"oidc_failure\"}");

            await store.AddAsync(entry, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略审计异常。
        }
    }
}
