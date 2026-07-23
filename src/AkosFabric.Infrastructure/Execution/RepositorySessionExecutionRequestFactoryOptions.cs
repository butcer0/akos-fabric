namespace AkosFabric.Infrastructure.Execution;

public sealed class RepositorySessionExecutionRequestFactoryOptions
{
    public Uri OpenTelemetryEndpoint { get; init; } =
        new("http://127.0.0.1:4317");

    public void Validate()
    {
        if (!OpenTelemetryEndpoint.IsAbsoluteUri ||
            OpenTelemetryEndpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(OpenTelemetryEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(OpenTelemetryEndpoint.Query) ||
            !string.IsNullOrEmpty(OpenTelemetryEndpoint.Fragment))
        {
            throw new ArgumentException(
                "The OpenTelemetry endpoint must be an absolute HTTP(S) URI " +
                "without user information, query, or fragment.",
                nameof(OpenTelemetryEndpoint));
        }
    }
}

