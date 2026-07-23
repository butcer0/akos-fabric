namespace AkosFabric.Api.Configuration;

public sealed class SourceControlHostOptions
{
    public GitHubHostOptions GitHub { get; init; } = new();
}

public sealed class GitHubHostOptions
{
    public bool Enabled { get; init; }

    public string AuthenticationProfile { get; init; } = string.Empty;

    public Uri ApiBaseUrl { get; init; } =
        new("https://api.github.com/");

    public string AppId { get; init; } = string.Empty;

    public long InstallationId { get; init; }

    public string PrivateKeyPath { get; init; } = string.Empty;

    public string UserAgent { get; init; } = "akos-fabric/1.4";
}
