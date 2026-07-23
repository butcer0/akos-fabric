using Microsoft.AspNetCore.Authorization;

namespace AkosFabric.Api.Security;

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            ScopeClaims.ContainsScope(context.User, requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
