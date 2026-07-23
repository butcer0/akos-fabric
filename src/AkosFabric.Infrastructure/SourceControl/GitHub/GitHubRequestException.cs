using System.Net;

namespace AkosFabric.Infrastructure.SourceControl.GitHub;

public sealed class GitHubRequestException : Exception
{
    public GitHubRequestException(string operation, HttpStatusCode statusCode)
        : base(
            $"GitHub operation '{operation}' failed with HTTP status {(int)statusCode}.")
    {
        Operation = operation;
        StatusCode = statusCode;
    }

    public string Operation { get; }

    public HttpStatusCode StatusCode { get; }
}
