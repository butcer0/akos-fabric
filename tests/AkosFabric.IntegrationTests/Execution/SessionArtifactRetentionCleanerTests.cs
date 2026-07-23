using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.IntegrationTests.Execution;

public sealed class SessionArtifactRetentionCleanerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 18, 0, 0, TimeSpan.Zero);

    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "akos-retention-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SchedulingDeletesCredentialAndStartsSevenDayRetention()
    {
        SessionFileStore store = CreateStore();
        Guid sessionId = Guid.NewGuid();
        await store.ReplaceCredentialAsync(
            sessionId,
            new SourceControlCredential(
                "user",
                "secret",
                Now.AddMinutes(30)),
            CancellationToken.None);
        var cleaner = CreateCleaner(store, Now.AddDays(6));

        await cleaner.ScheduleAsync(
            sessionId,
            Now,
            CancellationToken.None);
        SessionArtifactRetentionCandidate candidate = Assert.Single(
            await cleaner.ListAsync(CancellationToken.None));

        Assert.False(
            File.Exists(
                store.GetSessionFile(
                    sessionId,
                    SessionFileStore.CredentialFileName)));
        Assert.Equal(Now, candidate.RetentionStartedAt);
        Assert.Equal(Now.AddDays(7), candidate.DeleteAfter);
        Assert.False(candidate.IsDue);
    }

    [Fact]
    public async Task DryRunListsDueDirectoryWithoutDeletingIt()
    {
        SessionFileStore store = CreateStore();
        Guid sessionId = Guid.NewGuid();
        store.EnsureSessionDirectory(sessionId);
        var cleaner = CreateCleaner(store, Now);
        await cleaner.ScheduleAsync(
            sessionId,
            Now.AddDays(-8),
            CancellationToken.None);

        SessionArtifactCleanupResult result = await cleaner.CleanupAsync(
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.True(Assert.Single(result.Candidates).IsDue);
        Assert.Empty(result.DeletedSessionIds);
        Assert.True(Directory.Exists(store.GetSessionDirectory(sessionId)));
    }

    [Fact]
    public async Task CleanupDeletesOnlyDueCanonicalSessionDirectories()
    {
        SessionFileStore store = CreateStore();
        Guid dueId = Guid.NewGuid();
        Guid retainedId = Guid.NewGuid();
        store.EnsureSessionDirectory(dueId);
        store.EnsureSessionDirectory(retainedId);
        string unmanaged = Path.Combine(rootDirectory, "operator-notes");
        Directory.CreateDirectory(unmanaged);
        Directory.SetLastWriteTimeUtc(unmanaged, Now.AddYears(-1).UtcDateTime);
        var cleaner = CreateCleaner(store, Now);
        await cleaner.ScheduleAsync(
            dueId,
            Now.AddDays(-8),
            CancellationToken.None);
        await cleaner.ScheduleAsync(
            retainedId,
            Now.AddDays(-6),
            CancellationToken.None);

        SessionArtifactCleanupResult result = await cleaner.CleanupAsync(
            dryRun: false,
            CancellationToken.None);

        Assert.Equal([dueId], result.DeletedSessionIds);
        Assert.False(Directory.Exists(store.GetSessionDirectory(dueId)));
        Assert.True(Directory.Exists(store.GetSessionDirectory(retainedId)));
        Assert.True(Directory.Exists(unmanaged));
    }

    [Fact]
    public async Task MissingSessionDirectoryIsNotCreatedWhenScheduled()
    {
        SessionFileStore store = CreateStore();
        Guid sessionId = Guid.NewGuid();
        var cleaner = CreateCleaner(store, Now);

        await cleaner.ScheduleAsync(
            sessionId,
            Now,
            CancellationToken.None);

        Assert.False(Directory.Exists(rootDirectory));
        Assert.Empty(await cleaner.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CleanupRejectsNestedReparsePointWithoutDeletingTarget()
    {
        SessionFileStore store = CreateStore();
        Guid sessionId = Guid.NewGuid();
        string sessionDirectory = store.EnsureSessionDirectory(sessionId);
        await store.ReplaceCredentialAsync(
            sessionId,
            new SourceControlCredential(
                "user",
                "must-be-deleted",
                Now.AddMinutes(30)),
            CancellationToken.None);
        string external = Path.Combine(
            Path.GetTempPath(),
            "akos-retention-external",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        string proof = Path.Combine(external, "proof.txt");
        await File.WriteAllTextAsync(proof, "must survive");
        string link = Path.Combine(sessionDirectory, "external-link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException
                    or PlatformNotSupportedException
                    or IOException)
            {
                return;
            }

            Directory.SetLastWriteTimeUtc(
                sessionDirectory,
                Now.AddDays(-8).UtcDateTime);
            var cleaner = CreateCleaner(store, Now);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cleaner.CleanupAsync(
                    dryRun: false,
                    CancellationToken.None));

            Assert.True(File.Exists(proof));
            Assert.True(Directory.Exists(sessionDirectory));
            Assert.False(
                File.Exists(
                    store.GetSessionFile(
                        sessionId,
                        SessionFileStore.CredentialFileName)));
        }
        finally
        {
            if (Directory.Exists(sessionDirectory))
            {
                Directory.Delete(sessionDirectory, recursive: true);
            }

            if (Directory.Exists(external))
            {
                Directory.Delete(external, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsNonPositiveRetentionPeriod()
    {
        SessionFileStore store = CreateStore();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SessionArtifactRetentionCleaner(
                store,
                new SessionArtifactRetentionOptions
                {
                    RetentionPeriod = TimeSpan.Zero,
                }));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SessionFileStore CreateStore() =>
        new(
            new SessionFileStoreOptions
            {
                RootDirectory = rootDirectory,
                OwnerUserId = SessionFileStore.GetEffectiveUserId(),
                OwnerGroupId = SessionFileStore.GetEffectiveGroupId(),
            });

    private static SessionArtifactRetentionCleaner CreateCleaner(
        SessionFileStore store,
        DateTimeOffset now) =>
        new(
            store,
            new SessionArtifactRetentionOptions(),
            new FixedTimeProvider(now));

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
