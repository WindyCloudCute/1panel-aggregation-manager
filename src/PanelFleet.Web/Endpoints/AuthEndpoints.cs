using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", (HttpContext context) =>
            context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect("/")
                : Results.Content(HtmlPages.Login, "text/html; charset=utf-8"))
            .AllowAnonymous();

        app.MapGet("/auth/login", (string? returnUrl) =>
            {
                var target = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = target },
                    [OpenIdConnectDefaults.AuthenticationScheme]);
            })
            .AllowAnonymous();

        app.MapPost("/logout", async (
                HttpContext context,
                IAntiforgery antiforgery,
                IActorProvider actors,
                IAuditStore auditStore,
                CancellationToken cancellationToken) =>
            {
                if (!await Csrf.ValidateAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Csrf.Reject();
                }

                try
                {
                    var actor = await actors.GetAsync().ConfigureAwait(false);
                    await auditStore.AddAsync(
                        new AuditEntry(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            actor,
                            "auth.logout",
                            "user",
                            actor.Subject,
                            null,
                            null),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 审计失败不阻断登出。
                }

                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                return Results.Redirect("/login");
            })
            .RequireAuthorization();

        app.MapGet(
                "/auth/denied",
                () => Results.Content(
                    HtmlPages.Denied,
                    "text/html; charset=utf-8",
                    statusCode: StatusCodes.Status403Forbidden))
            .AllowAnonymous();

        app.MapGet(
                "/error",
                () => Results.Content(
                    HtmlPages.Error,
                    "text/html; charset=utf-8",
                    statusCode: StatusCodes.Status500InternalServerError))
            .AllowAnonymous();

        return app;
    }

    private static class HtmlPages
    {
        private const string Head =
            """
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>1Panel Aggregation Manager</title>
              <link rel="stylesheet" href="/app.css" />
            </head>
            """;

        public const string Login =
            Head +
            """
            <body class="auth-page">
              <main class="auth-card">
                <h1>1Panel Aggregation Manager</h1>
                <p class="muted">1Panel 多服务器聚合管理控制台</p>
                <a class="btn btn-primary" href="/auth/login">使用 Pocket ID 登录</a>
                <p class="muted small">本工具仅支持 OIDC 登录，无本地账号密码。</p>
              </main>
            </body>
            </html>
            """;

        public const string Denied =
            Head +
            """
            <body class="auth-page">
              <main class="auth-card">
                <h1>没有访问权限</h1>
                <p>当前账号未获授权使用 1Panel Aggregation Manager。</p>
                <p class="muted">请联系 Pocket ID 管理员确认客户端访问权限或用户组。</p>
                <a class="btn" href="/login">重新登录</a>
              </main>
            </body>
            </html>
            """;

        public const string Error =
            Head +
            """
            <body class="auth-page">
              <main class="auth-card">
                <h1>请求处理失败</h1>
                <p>请稍后重试；若持续失败，请查看服务日志。</p>
                <a class="btn" href="/">返回首页</a>
              </main>
            </body>
            </html>
            """;
    }
}
