using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AkosFabric.Api.Security;

public static class RequiredAccessTokenClaimsValidator
{
    public static Task ValidateAsync(TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Principal is null ||
            !HasRequiredClaims(context.Principal))
        {
            context.Fail("The access token is missing one or more required claims.");
        }

        return Task.CompletedTask;
    }

    public static bool HasRequiredClaims(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return HasValue(principal, "sub") &&
               HasNumericDate(principal, "iat") &&
               HasValue(principal, "jti") &&
               (HasValue(principal, "client_id") || HasValue(principal, "azp")) &&
               ScopeClaims.ContainsAny(principal);
    }

    private static bool HasValue(ClaimsPrincipal principal, string claimType) =>
        principal.Claims.Any(
            claim =>
                string.Equals(claim.Type, claimType, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(claim.Value));

    private static bool HasNumericDate(ClaimsPrincipal principal, string claimType) =>
        principal.Claims.Any(
            claim =>
                string.Equals(claim.Type, claimType, StringComparison.Ordinal) &&
                long.TryParse(
                    claim.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _));
}
