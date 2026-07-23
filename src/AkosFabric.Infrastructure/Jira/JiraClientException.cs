using System.Net;

namespace AkosFabric.Infrastructure.Jira;

public sealed class JiraClientException : Exception
{
    public JiraClientException(
        string message,
        JiraFailureKind failureKind,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        StatusCode = statusCode;
    }

    public JiraFailureKind FailureKind { get; }

    public HttpStatusCode? StatusCode { get; }
}

public enum JiraFailureKind
{
    Transient,
    Terminal,
}
