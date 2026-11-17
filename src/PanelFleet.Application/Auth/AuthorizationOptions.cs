using System.Security.Claims;

namespace PanelFleet.Application.Auth;

/// <summary>授权配置：白名单为第二道防线，角色映射由 Pocket ID 的 groups claim 提供。</summary>
public sealed class AuthorizationOptions
{
    public IReadOnlyList<string> AllowedEmails { get; set; } = [];

    public IReadOnlyList<string> AllowedSubjects { get; set; } = [];

    public string RoleClaimType { get; set; } = "groups";

    public IReadOnlyList<string> AdminRoles { get; set; } = [];

    public IReadOnlyList<string> ReadOnlyRoles { get; set; } = [];
}

public sealed record AuthorizationDecision(bool Allowed, bool IsAdmin, string? Reason);

public static class AuthorizationEvaluator
{
    public static AuthorizationDecision Evaluate(ClaimsPrincipal? principal, AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return new AuthorizationDecision(false, false, "unauthenticated");
        }

        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst("email")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value;

        var allowListEnabled = options.AllowedEmails.Count > 0 || options.AllowedSubjects.Count > 0;
        if (allowListEnabled && !IsInAllowList(subject, email, options))
        {
            return new AuthorizationDecision(false, false, "not_allowlisted");
        }

        if (options.AdminRoles.Count == 0)
        {
            return new AuthorizationDecision(true, true, null);
        }

        var roles = principal.FindAll(options.RoleClaimType)
            .Select(claim => claim.Value)
            .Concat(principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value))
            .ToArray();

        if (roles.Any(role => options.AdminRoles.Any(admin => string.Equals(admin, role, StringComparison.OrdinalIgnoreCase))))
        {
            return new AuthorizationDecision(true, true, null);
        }

        if (roles.Any(role => options.ReadOnlyRoles.Any(readonlyRole => string.Equals(readonlyRole, role, StringComparison.OrdinalIgnoreCase))))
        {
            return new AuthorizationDecision(true, false, "read_only");
        }

        return new AuthorizationDecision(false, false, "not_in_role");
    }

    private static bool IsInAllowList(string? subject, string? email, AuthorizationOptions options) =>
        (subject is not null && options.AllowedSubjects.Any(item => string.Equals(item, subject, StringComparison.OrdinalIgnoreCase)))
        || (email is not null && options.AllowedEmails.Any(item => string.Equals(item, email, StringComparison.OrdinalIgnoreCase)));
}
