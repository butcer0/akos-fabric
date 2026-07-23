namespace AkosFabric.Infrastructure.SourceControl.GitHub;

public sealed class GitHubOptionsException : Exception
{
    public GitHubOptionsException(string message)
        : base(message)
    {
    }

    public GitHubOptionsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
