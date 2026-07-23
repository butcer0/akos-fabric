using AkosFabric.Application.AgentExecution.Interfaces;

namespace AkosFabric.Infrastructure.Execution;

public sealed class SessionArtifactRetentionCleaner
    : ISessionArtifactRetentionScheduler
{
    private readonly SessionFileStore fileStore;
    private readonly SessionArtifactRetentionOptions options;
    private readonly TimeProvider timeProvider;

    public SessionArtifactRetentionCleaner(
        SessionFileStore fileStore,
        SessionArtifactRetentionOptions options,
        TimeProvider? timeProvider = null)
    {
        this.fileStore =
            fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        this.options =
            options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task ScheduleAsync(
        Guid repositorySessionId,
        DateTimeOffset sessionFinishedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileStore.ScheduleRetention(
            repositorySessionId,
            sessionFinishedAt.ToUniversalTime());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionArtifactRetentionCandidate>> ListAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset evaluatedAt = timeProvider.GetUtcNow();
        return Task.FromResult<
            IReadOnlyList<SessionArtifactRetentionCandidate>>(
            BuildCandidates(
                evaluatedAt,
                deleteCredentialsBeforeValidation: false,
                cancellationToken));
    }

    public Task<SessionArtifactCleanupResult> CleanupAsync(
        bool dryRun,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset evaluatedAt = timeProvider.GetUtcNow();
        List<SessionArtifactRetentionCandidate> candidates =
            BuildCandidates(
                evaluatedAt,
                deleteCredentialsBeforeValidation: !dryRun,
                cancellationToken);
        var deleted = new List<Guid>();
        if (!dryRun)
        {
            foreach (SessionArtifactRetentionCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidate.IsDue)
                {
                    continue;
                }

                fileStore.DeleteSessionDirectory(
                    candidate.RepositorySessionId);
                deleted.Add(candidate.RepositorySessionId);
            }
        }

        return Task.FromResult(
            new SessionArtifactCleanupResult(
                dryRun,
                evaluatedAt,
                candidates,
                deleted));
    }

    private List<SessionArtifactRetentionCandidate> BuildCandidates(
        DateTimeOffset evaluatedAt,
        bool deleteCredentialsBeforeValidation,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SessionArtifactRetentionCandidate>();
        foreach (SessionDirectoryEntry directory in
                 fileStore.ListSessionDirectories(
                     deleteCredentialsBeforeValidation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset deleteAfter =
                directory.RetentionStartedAt.Add(options.RetentionPeriod);
            candidates.Add(
                new SessionArtifactRetentionCandidate(
                    directory.RepositorySessionId,
                    directory.DirectoryPath,
                    directory.RetentionStartedAt,
                    deleteAfter,
                    deleteAfter <= evaluatedAt));
        }

        return candidates;
    }
}
