using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Application.AgentExecution.Models;

public sealed record RepositorySessionExecutionRequest(
    Guid RepositorySessionId,
    string RepositoryProfile,
    ReadOnlyMemory<byte> ManifestJson,
    SourceControlCredential SourceControlCredential,
    string ImageReference,
    string ImageDigest,
    Uri OpenTelemetryEndpoint,
    string? TraceParent,
    string LlmProvider,
    string LlmModel,
    string GeminiApiKey);

public sealed record RepositorySessionExecution(
    Guid RepositorySessionId,
    string ContainerName,
    string ContainerId,
    string SessionDirectory,
    bool Reattached);

public sealed record RepositorySessionContainer(
    Guid RepositorySessionId,
    string RepositoryProfile,
    string ContainerName,
    string ContainerId,
    RepositorySessionContainerState State,
    int ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record RepositorySessionWaitResult(
    bool TimedOut,
    int? ExitCode);

public enum RepositorySessionContainerState
{
    Created,
    Restarting,
    Running,
    Removing,
    Paused,
    Exited,
    Dead,
    Unknown,
}
