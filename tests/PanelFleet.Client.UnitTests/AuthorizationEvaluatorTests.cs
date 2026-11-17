using System.Security.Claims;
using PanelFleet.Application.Auth;

namespace PanelFleet.Client.UnitTests;

public sealed class AuthorizationEvaluatorTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void Unauthenticated_IsDenied()
    {
        var decision = AuthorizationEvaluator.Evaluate(new ClaimsPrincipal(new ClaimsIdentity()), new AuthorizationOptions());

        Assert.False(decision.Allowed);
        Assert.Equal("unauthenticated", decision.Reason);
    }

    [Fact]
    public void DefaultOptions_AllowsAnyAuthenticatedUserAsAdmin()
    {
        var decision = AuthorizationEvaluator.Evaluate(
            Principal(new Claim("sub", "user-1"), new Claim("email", "user@example.com")),
            new AuthorizationOptions());

        Assert.True(decision.Allowed);
        Assert.True(decision.IsAdmin);
    }

    [Fact]
    public void AllowListMismatch_IsDenied()
    {
        var options = new AuthorizationOptions { AllowedEmails = ["other@example.com"] };

        var decision = AuthorizationEvaluator.Evaluate(
            Principal(new Claim("sub", "user-1"), new Claim("email", "user@example.com")),
            options);

        Assert.False(decision.Allowed);
        Assert.Equal("not_allowlisted", decision.Reason);
    }

    [Fact]
    public void AllowListMatch_IsAllowed()
    {
        var options = new AuthorizationOptions { AllowedEmails = ["user@example.com"] };

        var decision = AuthorizationEvaluator.Evaluate(
            Principal(new Claim("sub", "user-1"), new Claim("email", "user@example.com")),
            options);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void AdminRoleMatch_IsAdmin()
    {
        var options = new AuthorizationOptions { AdminRoles = ["panel-admins"], RoleClaimType = "groups" };

        var decision = AuthorizationEvaluator.Evaluate(
            Principal(new Claim("sub", "user-1"), new Claim("groups", "panel-admins")),
            options);

        Assert.True(decision.Allowed);
        Assert.True(decision.IsAdmin);
    }

    [Fact]
    public void MissingAdminRole_IsDenied()
    {
        var options = new AuthorizationOptions { AdminRoles = ["panel-admins"], RoleClaimType = "groups" };

        var decision = AuthorizationEvaluator.Evaluate(
            Principal(new Claim("sub", "user-1"), new Claim("groups", "another-group")),
            options);

        Assert.False(decision.Allowed);
        Assert.Equal("not_in_role", decision.Reason);
    }

    [Fact]
    public void ReadOnlyRole_IsAllowedButNotAdmin()
    {
        var options = new AuthorizationOptions
        {
            AdminRoles = ["panel-admins"],
            ReadOnlyRoles = ["panel-readonly"],
            RoleClaimType = "groups"
        };

        var decision = AuthorizationEvaluator.Evaluate(
            Principal(new Claim("sub", "user-1"), new Claim("groups", "panel-readonly")),
            options);

        Assert.True(decision.Allowed);
        Assert.False(decision.IsAdmin);
        Assert.Equal("read_only", decision.Reason);
    }
}
