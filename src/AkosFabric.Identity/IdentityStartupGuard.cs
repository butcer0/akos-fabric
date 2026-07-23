namespace AkosFabric.Identity;

public static class IdentityStartupGuard
{
    public static void Validate(
        IdentityHostSettings settings,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);

        if (settings.Mode is not (
            IdentityHostSettings.DevelopmentMode or
            IdentityHostSettings.ProductionMode))
        {
            throw new InvalidOperationException(
                "Identity:Mode must be either Development or Production.");
        }

        if (!Uri.TryCreate(settings.IssuerUri, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException(
                "Identity:IssuerUri must be a non-empty absolute URI.");
        }

        if (environment.IsProduction() &&
            settings.Mode == IdentityHostSettings.DevelopmentMode)
        {
            throw new InvalidOperationException(
                "Identity:Mode=Development is forbidden in the Production environment.");
        }

        if (environment.IsProduction() && !settings.RequireHttpsMetadata)
        {
            throw new InvalidOperationException(
                "Identity:RequireHttpsMetadata=false is forbidden in the Production environment.");
        }

        if (settings.Mode == IdentityHostSettings.ProductionMode &&
            !string.Equals(issuer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The production Identity:IssuerUri must use HTTPS.");
        }

        if (settings.Mode != IdentityHostSettings.DevelopmentMode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.DevelopmentClientSecret))
        {
            throw new InvalidOperationException(
                "Identity:Development:ClientSecret must be supplied outside source control.");
        }

        if (settings.DevelopmentClientSecret.Length < 32)
        {
            throw new InvalidOperationException(
                "Identity:Development:ClientSecret must contain at least 32 characters.");
        }

        ValidateExternalPath(
            settings.DevelopmentSigningKeyPath,
            "Identity:Development:SigningKeyPath",
            environment.ContentRootPath);
        ValidateExternalPath(
            settings.DevelopmentDataProtectionKeyPath,
            "Identity:Development:DataProtectionKeyPath",
            environment.ContentRootPath);
    }

    private static void ValidateExternalPath(
        string? configuredPath,
        string settingName,
        string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                $"{settingName} must be supplied and must point outside the source repository.");
        }

        if (!Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException($"{settingName} must be an absolute path.");
        }

        var repositoryRoot = FindRepositoryRoot(contentRootPath);
        var fullRepositoryRoot = EnsureTrailingSeparator(Path.GetFullPath(repositoryRoot));
        var fullConfiguredPath = Path.GetFullPath(configuredPath);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (fullConfiguredPath.StartsWith(fullRepositoryRoot, pathComparison) ||
            string.Equals(
                fullConfiguredPath,
                fullRepositoryRoot.TrimEnd(Path.DirectorySeparatorChar),
                pathComparison))
        {
            throw new InvalidOperationException(
                $"{settingName} must point outside the source repository.");
        }
    }

    private static string FindRepositoryRoot(string contentRootPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(contentRootPath));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "AkosFabric.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(contentRootPath);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
