namespace AkosFabric.Infrastructure.Execution;

public sealed record SessionArtifactRetentionCandidate(
    Guid RepositorySessionId,
    string DirectoryPath,
    DateTimeOffset RetentionStartedAt,
    DateTimeOffset DeleteAfter,
    bool IsDue);

public sealed record SessionArtifactCleanupResult(
    bool DryRun,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<SessionArtifactRetentionCandidate> Candidates,
    IReadOnlyList<Guid> DeletedSessionIds);
