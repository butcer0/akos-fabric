using System.Security.Claims;

namespace AkosFabric.Api.Security;

public static class ScopeClaims
{
    private static readonly string[] ClaimTypes = ["scope", "scp"];

    public static bool ContainsAny(ClaimsPrincipal principal) =>
        Values(principal).Any();

    public static bool ContainsScope(ClaimsPrincipal principal, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredScope);

        return Values(principal).Contains(requiredScope, StringComparer.Ordinal);
    }

    private static IEnumerable<string> Values(ClaimsPrincipal principal) =>
        principal.Claims
            .Where(claim => ClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
            .SelectMany(claim => claim.Value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
}
