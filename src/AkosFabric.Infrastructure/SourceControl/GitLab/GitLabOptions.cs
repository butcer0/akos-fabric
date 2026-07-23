namespace AkosFabric.Infrastructure.SourceControl.GitLab;

public sealed class GitLabOptions
{
    public Uri ApiBaseUrl { get; init; } =
        new("https://gitlab.com/api/v4/");

    public string AuthenticationProfile { get; init; } = string.Empty;
}
