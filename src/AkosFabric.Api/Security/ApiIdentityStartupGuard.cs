namespace AkosFabric.Api.Security;

public static class ApiIdentityStartupGuard
{
    public static void Validate(
        ApiIdentitySettings settings,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);

        if (settings.Mode is not (
            ApiIdentitySettings.DevelopmentMode or
            ApiIdentitySettings.ProductionMode))
        {
            throw new InvalidOperationException(
                "Identity:Mode must be either Development or Production.");
        }

        if (!Uri.TryCreate(settings.Authority, UriKind.Absolute, out var authority))
        {
            throw new InvalidOperationException(
                "Identity:Authority must be a non-empty absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(settings.Audience))
        {
            throw new InvalidOperationException("Identity:Audience must not be empty.");
        }

        if (environment.IsProduction() &&
            settings.Mode == ApiIdentitySettings.DevelopmentMode)
        {
            throw new InvalidOperationException(
                "Identity:Mode=Development is forbidden in the Production environment.");
        }

        if (environment.IsProduction() && !settings.RequireHttpsMetadata)
        {
            throw new InvalidOperationException(
                "Identity:RequireHttpsMetadata=false is forbidden in the Production environment.");
        }

        if (settings.Mode == ApiIdentitySettings.ProductionMode &&
            !string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The production Identity:Authority must use HTTPS.");
        }
    }
}
