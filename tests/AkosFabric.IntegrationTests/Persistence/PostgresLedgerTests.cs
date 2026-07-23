using System.Text.Json;

using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Domain.Ledger;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Domain.WorkItems;
using AkosFabric.Infrastructure.Persistence;

using Npgsql;
using NpgsqlTypes;

namespace AkosFabric.IntegrationTests.Persistence;

public sealed class PostgresLedgerTests : IAsyncLifetime
{
    private const string TestSubject = "integration-test-subject";

    private NpgsqlDataSource dataSource = null!;
    private PostgresRepositorySessionRepository repository = null!;

    public async Task InitializeAsync()
    {
        dataSource = NpgsqlDataSource.Create(GetConnectionString());
        await new PostgresMigrationRunner(dataSource)
            .MigrateAsync(CancellationToken.None);
        await DeleteOwnedTestSessionsAsync();
        repository = new PostgresRepositorySessionRepository(dataSource);
    }

    public async Task DisposeAsync()
    {
        if (dataSource is null)
        {
            return;
        }

        await DeleteOwnedTestSessionsAsync();
        await dataSource.DisposeAsync();
    }

    [Fact]
    public async Task MigrationIsIdempotentlyTracked()
    {
        var runner = new PostgresMigrationRunner(dataSource);

        await runner.MigrateAsync(CancellationToken.None);
        await runner.MigrateAsync(CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM akos_fabric_schema_migration
            WHERE version = '001_InitialLedger';
            """,
            connection);

        var count = (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Migration count was null."));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateWritesSessionOrderedItemsAndLedgerAtomically()
    {
        var creation = CreateSession(itemCount: 2);

        await repository.CreateAsync(creation, CancellationToken.None);

        var persisted = await repository.FindAsync(
            creation.Id,
            CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(RepositorySessionStatus.Created, persisted.Status);
        Assert.Equal(creation.RepositoryProfile, persisted.RepositoryProfile);
        Assert.Equal(creation.MessageId, persisted.MessageId);
        using var expectedPayload =
            JsonDocument.Parse(creation.RequestPayloadJson);
        using var actualPayload =
            JsonDocument.Parse(persisted.RequestPayloadJson);
        Assert.True(
            JsonElement.DeepEquals(
                expectedPayload.RootElement,
                actualPayload.RootElement));

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (
                    SELECT array_agg(sequence_number ORDER BY sequence_number)
                    FROM work_item_run
                    WHERE repository_session_id = $1
                ),
                (
                    SELECT count(*)
                    FROM ledger_entry
                    WHERE
                        repository_session_id = $1
                        AND entry_type = 'SESSION_CREATED'
                        AND work_item_run_id IS NULL
                );
            """,
            connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, creation.Id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal([1, 2], reader.GetFieldValue<int[]>(0));
        Assert.Equal(1, reader.GetInt64(1));
    }

    [Fact]
    public async Task RecoverableQueryReturnsOnlyMonitorableSessionStates()
    {
        RepositorySessionCreation created = CreateSession(itemCount: 1);
        RepositorySessionCreation running = CreateSession(itemCount: 1);
        await repository.CreateAsync(created, CancellationToken.None);
        await repository.CreateAsync(running, CancellationToken.None);
        await MoveToRunningAsync(running.Id);

        IReadOnlyList<RepositorySessionRecord> recoverable =
            await repository.ListRecoverableAsync(CancellationToken.None);

        Assert.Contains(recoverable, session => session.Id == running.Id);
        Assert.DoesNotContain(recoverable, session => session.Id == created.Id);
    }

    [Fact]
    public async Task OperatorListIsNewestFirstAndBounded()
    {
        RepositorySessionCreation older = CreateSession(itemCount: 1);
        RepositorySessionCreation newer = CreateSession(itemCount: 1) with
        {
            CreatedAt = older.CreatedAt.AddMinutes(1),
        };
        await repository.CreateAsync(older, CancellationToken.None);
        await repository.CreateAsync(newer, CancellationToken.None);

        IReadOnlyList<RepositorySessionRecord> sessions =
            await repository.ListAsync(2, CancellationToken.None);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(newer.Id, sessions[0].Id);
        Assert.Equal(older.Id, sessions[1].Id);
    }

    [Fact]
    public async Task JiraSynchronizationWarningIsDurableWithoutChangingStatus()
    {
        RepositorySessionCreation creation = CreateSession(itemCount: 1);
        await repository.CreateAsync(creation, CancellationToken.None);
        WorkItemRunCreation item = Assert.Single(creation.WorkItems);

        await repository.RecordJiraSynchronizationWarningAsync(
            new JiraSynchronizationWarningRecording(
                creation.Id,
                item.Id,
                "transition_review",
                "jira_transition_unavailable",
                """
                {
                  "integration": "jira",
                  "operation": "transition_review",
                  "failureCode": "jira_transition_unavailable",
                  "jiraKey": "KAN-1"
                }
                """,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        RepositorySessionRecord session = Assert.IsType<RepositorySessionRecord>(
            await repository.FindAsync(
                creation.Id,
                CancellationToken.None));
        Assert.Equal(RepositorySessionStatus.Created, session.Status);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM ledger_entry
            WHERE
                repository_session_id = $1
                AND work_item_run_id = $2
                AND entry_type = 'JIRA_SYNCHRONIZATION_WARNING'
                AND payload ->> 'failureCode' =
                    'jira_transition_unavailable';
            """,
            connection);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            creation.Id);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            item.Id);
        Assert.Equal(
            1,
            (long)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException(
                    "Warning count was null.")));
    }

    [Fact]
    public async Task CreateRollsBackSessionWhenAnyWorkItemFails()
    {
        var creation = CreateSession(itemCount: 2);
        creation = creation with
        {
            WorkItems =
            [
                creation.WorkItems[0],
                creation.WorkItems[1] with
                {
                    JiraKey = creation.WorkItems[0].JiraKey,
                },
            ],
        };

        await Assert.ThrowsAsync<PostgresException>(
            () => repository.CreateAsync(creation, CancellationToken.None));

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM repository_session
            WHERE id = $1;
            """,
            connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, creation.Id);

        Assert.Equal(0, (long)(await command.ExecuteScalarAsync() ?? -1L));
    }

    [Fact]
    public async Task SessionStatusAndLedgerEventCommitTogether()
    {
        var creation = CreateSession(itemCount: 1);
        await repository.CreateAsync(creation, CancellationToken.None);
        var occurredAt = DateTimeOffset.UtcNow;

        await repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                creation.Id,
                RepositorySessionStatus.Created,
                RepositorySessionStatus.Published,
                RunLedgerEventType.SessionPublished,
                """{"confirmed":true}""",
                occurredAt),
            CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                repository_session.status,
                ledger_entry.entry_type,
                ledger_entry.payload ->> 'confirmed'
            FROM repository_session
            JOIN ledger_entry
                ON ledger_entry.repository_session_id = repository_session.id
            WHERE
                repository_session.id = $1
                AND ledger_entry.entry_type = 'SESSION_PUBLISHED';
            """,
            connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, creation.Id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("published", reader.GetString(0));
        Assert.Equal("SESSION_PUBLISHED", reader.GetString(1));
        Assert.Equal("true", reader.GetString(2));
    }

    [Fact]
    public async Task SessionStatusRollsBackWhenLedgerInsertFails()
    {
        var creation = CreateSession(itemCount: 1);
        await repository.CreateAsync(creation, CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(
            () => repository.TransitionSessionStatusAsync(
                new RepositorySessionStatusTransition(
                    creation.Id,
                    RepositorySessionStatus.Created,
                    RepositorySessionStatus.Published,
                    RunLedgerEventType.SessionPublished,
                    "not-json",
                    DateTimeOffset.UtcNow),
                CancellationToken.None));

        var persisted = await repository.FindAsync(
            creation.Id,
            CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(RepositorySessionStatus.Created, persisted.Status);
        Assert.Null(persisted.PublishedAt);
        Assert.Equal(
            0,
            await CountLedgerEntriesAsync(
                creation.Id,
                "SESSION_PUBLISHED"));
    }

    [Fact]
    public async Task WorkItemStatusAndLedgerEventCommitTogether()
    {
        var creation = CreateSession(itemCount: 1);
        await repository.CreateAsync(creation, CancellationToken.None);
        var workItemId = creation.WorkItems[0].Id;

        await repository.TransitionWorkItemStatusAsync(
            new WorkItemRunStatusTransition(
                creation.Id,
                workItemId,
                WorkItemRunStatus.Queued,
                WorkItemRunStatus.Planning,
                RunLedgerEventType.ItemStarted,
                "{}",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT work_item_run.status, ledger_entry.entry_type
            FROM work_item_run
            JOIN ledger_entry
                ON ledger_entry.work_item_run_id = work_item_run.id
            WHERE
                work_item_run.id = $1
                AND ledger_entry.entry_type = 'ITEM_STARTED';
            """,
            connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, workItemId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("planning", reader.GetString(0));
        Assert.Equal("ITEM_STARTED", reader.GetString(1));
    }

    [Fact]
    public async Task ResultOutcomeAndChangeRequestRecordingAreTransactionalAndIdempotent()
    {
        var creation = CreateSession(itemCount: 1);
        await repository.CreateAsync(creation, CancellationToken.None);
        await MoveToRunningAsync(creation.Id);
        Guid workItemId = creation.WorkItems[0].Id;
        string baseSha = new('a', 40);
        string candidateSha = new('b', 40);
        const string branchName = "agent/akos-1/12345678";
        var occurredAt = DateTimeOffset.UtcNow;
        var result = new AgentResultRecording(
            creation.Id,
            occurredAt,
            [
                new AgentWorkItemOutcomeRecording(
                    workItemId,
                    WorkItemRunStatus.BranchPushed,
                    baseSha,
                    branchName,
                    candidateSha,
                    """{"schema_version":"1.0"}""",
                    """{"schema_version":"1.0"}""",
                    """{"passed":true,"commands":[]}""",
                    $$"""{"candidate_sha":"{{candidateSha}}","disposition":"accept"}""",
                    """{"provider":"gemini","modelId":"gemini-3.6-flash"}""",
                    null,
                    null,
                    """{"status":"branch_pushed"}"""),
            ]);

        await repository.RecordValidatedResultAsync(
            result,
            CancellationToken.None);
        await repository.RecordValidatedResultAsync(
            result,
            CancellationToken.None);

        RepositorySessionRecord? processing = await repository.FindAsync(
            creation.Id,
            CancellationToken.None);
        Assert.NotNull(processing);
        Assert.Equal(
            RepositorySessionStatus.ProcessingResults,
            processing.Status);
        WorkItemRunRecord pushed = Assert.Single(
            await repository.ListWorkItemsAsync(
                creation.Id,
                CancellationToken.None));
        Assert.Equal(WorkItemRunStatus.BranchPushed, pushed.Status);
        Assert.Equal(baseSha, pushed.BaseCommitSha);
        Assert.Equal(branchName, pushed.BranchName);
        Assert.Equal(candidateSha, pushed.CandidateCommitSha);
        Assert.Equal(
            1,
            await CountLedgerEntriesAsync(creation.Id, "BRANCH_PUSHED"));

        var changeRequest = new ChangeRequestRecording(
            creation.Id,
            workItemId,
            branchName,
            candidateSha,
            new AkosFabric.Application.SourceControl.Models.ChangeRequestReference(
                "github",
                "provider-42",
                "42",
                new Uri("https://github.example/change/42"),
                candidateSha),
            occurredAt.AddMinutes(1));
        await repository.RecordChangeRequestAsync(
            changeRequest,
            CancellationToken.None);
        await repository.RecordChangeRequestAsync(
            changeRequest,
            CancellationToken.None);

        WorkItemRunRecord completedItem = Assert.Single(
            await repository.ListWorkItemsAsync(
                creation.Id,
                CancellationToken.None));
        Assert.Equal(
            WorkItemRunStatus.ChangeRequestCreated,
            completedItem.Status);
        Assert.Equal("provider-42", completedItem.ChangeRequestId);
        Assert.Equal("42", completedItem.ChangeRequestNumber);
        Assert.Equal(
            "https://github.example/change/42",
            completedItem.ChangeRequestUrl);
        Assert.Equal(
            1,
            await CountLedgerEntriesAsync(
                creation.Id,
                "CHANGE_REQUEST_CREATED"));

        var completion = new AgentResultProcessingCompletion(
            creation.Id,
            RepositorySessionStatus.Completed,
            null,
            null,
            """{"status":"completed"}""",
            occurredAt.AddMinutes(2));
        await repository.CompleteResultProcessingAsync(
            completion,
            CancellationToken.None);
        await repository.CompleteResultProcessingAsync(
            completion,
            CancellationToken.None);

        RepositorySessionRecord? completed = await repository.FindAsync(
            creation.Id,
            CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(RepositorySessionStatus.Completed, completed.Status);
        Assert.Equal(
            1,
            await CountLedgerEntriesAsync(
                creation.Id,
                "SESSION_COMPLETED"));
    }

    [Fact]
    public async Task CancellationTerminalizesItemsAndPermitsRetryWithFreshRows()
    {
        var original = CreateSession(itemCount: 2);
        await repository.CreateAsync(original, CancellationToken.None);
        var occurredAt = DateTimeOffset.UtcNow;

        await repository.CancelAsync(
            new RepositorySessionCancellation(
                original.Id,
                RepositorySessionStatus.Created,
                """{"reason":"operator_request"}""",
                occurredAt),
            CancellationToken.None);

        RepositorySessionRecord? cancelled = await repository.FindAsync(
            original.Id,
            CancellationToken.None);
        Assert.NotNull(cancelled);
        Assert.Equal(RepositorySessionStatus.Cancelled, cancelled.Status);
        IReadOnlyList<WorkItemRunRecord> cancelledItems =
            await repository.ListWorkItemsAsync(
                original.Id,
                CancellationToken.None);
        Assert.Equal([1, 2], cancelledItems.Select(item => item.SequenceNumber));
        Assert.All(
            cancelledItems,
            item =>
            {
                Assert.Equal(WorkItemRunStatus.Failed, item.Status);
                Assert.Equal("session_cancelled", item.FailureCode);
            });
        Assert.Equal(
            1,
            await CountLedgerEntriesAsync(
                original.Id,
                "SESSION_CANCELLED"));
        Assert.Equal(
            2,
            await CountLedgerEntriesAsync(
                original.Id,
                "ITEM_FAILED"));

        var retried = CreateSession(itemCount: 2) with
        {
            WorkItems = original.WorkItems
                .Select(
                    item => item with
                    {
                        Id = Guid.NewGuid(),
                    })
                .ToArray(),
        };
        await repository.CreateAsync(retried, CancellationToken.None);

        Assert.NotEqual(original.Id, retried.Id);
        Assert.NotEqual(original.MessageId, retried.MessageId);
        Assert.All(
            retried.WorkItems,
            retryItem => Assert.DoesNotContain(
                cancelledItems,
                cancelledItem => cancelledItem.Id == retryItem.Id));
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable(
            "AKOS_POSTGRES_CONNECTION_STRING")
        ?? "Host=127.0.0.1;Port=5432;Database=akos_fabric;" +
        "Username=akos_fabric;" +
        "Password=akos-local-postgres-7P3n6Nf4wR9k;" +
        "Timeout=5;Command Timeout=15";

    private static RepositorySessionCreation CreateSession(int itemCount)
    {
        var sessionId = Guid.NewGuid();
        var keySuffix = sessionId.ToString("N")[..10].ToUpperInvariant();
        var workItems = Enumerable.Range(1, itemCount)
            .Select(
                sequence => new WorkItemRunCreation(
                    Guid.NewGuid(),
                    sequence,
                    $"jira-id-{keySuffix}-{sequence}",
                    $"AKOS-{keySuffix}-{sequence}",
                    DateTimeOffset.UtcNow,
                    $$"""{"id":"jira-id-{{keySuffix}}-{{sequence}}"}"""))
            .ToArray();

        return new RepositorySessionCreation(
            sessionId,
            "akos-fabric",
            new string('a', 40),
            "github",
            "ghcr.io/example/akos-agent@sha256:" + new string('b', 64),
            "sha256:" + new string('b', 64),
            Guid.NewGuid(),
            """{"repositoryProfile":"akos-fabric","maxItems":2}""",
            TestSubject,
            "integration-test-client",
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            workItems);
    }

    private async Task<long> CountLedgerEntriesAsync(
        Guid repositorySessionId,
        string entryType)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM ledger_entry
            WHERE repository_session_id = $1 AND entry_type = $2;
            """,
            connection);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, entryType);
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }

    private async Task MoveToRunningAsync(Guid repositorySessionId)
    {
        var now = DateTimeOffset.UtcNow;
        await repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                repositorySessionId,
                RepositorySessionStatus.Created,
                RepositorySessionStatus.Published,
                RunLedgerEventType.SessionPublished,
                "{}",
                now),
            CancellationToken.None);
        await repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                repositorySessionId,
                RepositorySessionStatus.Published,
                RepositorySessionStatus.Starting,
                RunLedgerEventType.SessionStarting,
                "{}",
                now),
            CancellationToken.None);
        await repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                repositorySessionId,
                RepositorySessionStatus.Starting,
                RepositorySessionStatus.Running,
                RunLedgerEventType.SessionStarted,
                "{}",
                now),
            CancellationToken.None);
    }

    private async Task DeleteOwnedTestSessionsAsync()
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        foreach (var sql in GetTestCleanupStatements())
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, TestSubject);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string[] GetTestCleanupStatements() =>
    [
        """
        DELETE FROM ledger_entry
        WHERE repository_session_id IN (
            SELECT id
            FROM repository_session
            WHERE requested_by_subject = $1
        );
        """,
        """
        DELETE FROM work_item_run
        WHERE repository_session_id IN (
            SELECT id
            FROM repository_session
            WHERE requested_by_subject = $1
        );
        """,
        """
        DELETE FROM repository_session
        WHERE requested_by_subject = $1;
        """,
    ];
}
