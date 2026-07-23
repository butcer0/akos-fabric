using System.Security.Cryptography;

namespace AkosFabric.Infrastructure.SourceControl.GitHub;

public sealed class GitHubOptions
{
    public Uri ApiBaseUrl { get; init; } = new("https://api.github.com/");

    public string AppId { get; init; } = string.Empty;

    public long InstallationId { get; init; }

    public string PrivateKeyPath { get; init; } = string.Empty;

    public string UserAgent { get; init; } = "akos-fabric/1.4";

    public void Validate() => _ = ValidateAndReadPrivateKey();

    internal string ValidateAndReadPrivateKey()
    {
        ValidateApiOptions();

        if (string.IsNullOrWhiteSpace(PrivateKeyPath) ||
            !Path.IsPathFullyQualified(PrivateKeyPath))
        {
            throw new GitHubOptionsException(
                $"{nameof(PrivateKeyPath)} must be an absolute path.");
        }

        string privateKey;
        try
        {
            privateKey = File.ReadAllText(PrivateKeyPath);
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new GitHubOptionsException(
                $"{nameof(PrivateKeyPath)} must identify a readable RSA private key.",
                exception);
        }

        return privateKey;
    }

    internal void ValidateApiOptions()
    {
        if (!ApiBaseUrl.IsAbsoluteUri ||
            !string.Equals(ApiBaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(ApiBaseUrl.UserInfo) ||
            !string.IsNullOrEmpty(ApiBaseUrl.Query) ||
            !string.IsNullOrEmpty(ApiBaseUrl.Fragment))
        {
            throw new GitHubOptionsException(
                $"{nameof(ApiBaseUrl)} must be an absolute HTTPS URI without user information, query, or fragment.");
        }

        if (!ulong.TryParse(
                AppId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out ulong appId) ||
            appId == 0)
        {
            throw new GitHubOptionsException(
                $"{nameof(AppId)} must be a positive integer.");
        }

        if (InstallationId <= 0)
        {
            throw new GitHubOptionsException(
                $"{nameof(InstallationId)} must be positive.");
        }

        if (string.IsNullOrWhiteSpace(UserAgent) ||
            UserAgent.Any(char.IsControl))
        {
            throw new GitHubOptionsException(
                $"{nameof(UserAgent)} must contain a valid non-empty product identifier.");
        }
    }
}
