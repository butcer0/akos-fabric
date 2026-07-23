using System.Net.Http.Headers;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Infrastructure.SourceControl.GitHub;

internal static class GitHubHttp
{
    public static Uri CreateApiUri(Uri apiBaseUrl, string relativePath)
    {
        string baseUrl = apiBaseUrl.AbsoluteUri.TrimEnd('/');
        return new Uri($"{baseUrl}/{relativePath}", UriKind.Absolute);
    }

    public static void AddStandardHeaders(
        HttpRequestMessage request,
        string userAgent)
    {
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd(userAgent);
    }

    public static (string Owner, string Repository) ParseRepositoryId(
        string providerRepositoryId)
    {
        if (string.IsNullOrWhiteSpace(providerRepositoryId))
        {
            throw new ArgumentException(
                "A provider repository identifier is required.",
                nameof(providerRepositoryId));
        }

        string[] parts = providerRepositoryId.Split('/');
        if (parts.Length != 2 ||
            !IsValidRepositoryPart(parts[0]) ||
            !IsValidRepositoryPart(parts[1]))
        {
            throw new ArgumentException(
                "The GitHub repository identifier must be in 'owner/repository' form.",
                nameof(providerRepositoryId));
        }

        return (parts[0], parts[1]);
    }

    public static void ValidateRepositoryReference(
        SourceRepositoryReference repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (!string.Equals(
                repository.Provider,
                "github",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The repository must use the GitHub provider.",
                nameof(repository));
        }

        ParseRepositoryId(repository.ProviderRepositoryId);
        if (!repository.CloneUrl.IsAbsoluteUri ||
            !string.Equals(
                repository.CloneUrl.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(repository.CloneUrl.UserInfo) ||
            !string.IsNullOrEmpty(repository.CloneUrl.Query) ||
            !string.IsNullOrEmpty(repository.CloneUrl.Fragment))
        {
            throw new ArgumentException(
                "The GitHub clone URL must be an absolute HTTPS URI without embedded credentials, query, or fragment.",
                nameof(repository));
        }
    }

    public static void ValidateBranchName(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) ||
            branchName.Any(char.IsControl) ||
            branchName.StartsWith('/') ||
            branchName.EndsWith('/') ||
            branchName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A valid source branch name is required.",
                nameof(branchName));
        }
    }

    public static bool IsFullGitSha(string value) =>
        (value.Length is 40 or 64) &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidRepositoryPart(string value) =>
        value.Length is > 0 and <= 100 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');
}
