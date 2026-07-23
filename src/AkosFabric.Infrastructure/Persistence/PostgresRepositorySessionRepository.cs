using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Domain.Ledger;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Domain.WorkItems;

using Npgsql;
using NpgsqlTypes;

namespace AkosFabric.Infrastructure.Persistence;

public sealed class PostgresRepositorySessionRepository
    : IRepositorySessionRepository,
      IJiraSelectionRepository
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresRepositorySessionRepository(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource
            ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<RepositorySessionRecord?> FindAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                id,
                repository_profile,
                profile_revision_sha,
                source_control_provider,
                image_reference,
                image_digest,
                status,
                message_id,
                container_name,
                container_id,
                request_payload::text,
                requested_by_subject,
                requested_by_client_id,
                requested_by_token_id,
                trace_id,
                created_at,
                published_at,
                started_at,
                completed_at,
                failure_code,
                failure_message
            FROM repository_session
            WHERE id = $1;
            """,
            connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RepositorySessionRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseSessionStatus(reader.GetString(6)),
            reader.GetGuid(7),
            GetNullableString(reader, 8),
            GetNullableString(reader, 9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            GetNullableString(reader, 13),
            GetNullableString(reader, 14),
            reader.GetFieldValue<DateTimeOffset>(15),
            GetNullableDateTimeOffset(reader, 16),
            GetNullableDateTimeOffset(reader, 17),
            GetNullableDateTimeOffset(reader, 18),
            GetNullableString(reader, 19),
            GetNullableString(reader, 20));
    }

    public async Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                id,
                repository_session_id,
                sequence_number,
                jira_issue_id,
                jira_key,
                jira_updated_at,
                jira_snapshot::text,
                status,
                base_commit_sha,
                branch_name,
                candidate_commit_sha,
                change_request_id,
                change_request_number,
                change_request_url,
                started_at,
                completed_at,
                failure_code,
                failure_message
            FROM work_item_run
            WHERE repository_session_id = $1
            ORDER BY sequence_number;
            """,
            connection);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);

        var records = new List<WorkItemRunRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(
                new WorkItemRunRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetString(6),
                    ParseWorkItemStatus(reader.GetString(7)),
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9),
                    GetNullableString(reader, 10),
                    GetNullableString(reader, 11),
                    GetNullableString(reader, 12),
                    GetNullableString(reader, 13),
                    GetNullableDateTimeOffset(reader, 14),
                    GetNullableDateTimeOffset(reader, 15),
                    GetNullableString(reader, 16),
                    GetNullableString(reader, 17)));
        }

        return records;
    }

    public async Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "The session list limit must be between 1 and 200.");
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                id,
                repository_profile,
                profile_revision_sha,
                source_control_provider,
                image_reference,
                image_digest,
                status,
                message_id,
                container_name,
                container_id,
                request_payload::text,
                requested_by_subject,
                requested_by_client_id,
                requested_by_token_id,
                trace_id,
                created_at,
                published_at,
                started_at,
                completed_at,
                failure_code,
                failure_message
            FROM repository_session
            ORDER BY created_at DESC, id DESC
            LIMIT $1;
            """,
            connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, limit);

        var sessions = new List<RepositorySessionRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(ReadRepositorySession(reader));
        }

        return sessions;
    }

    public async Task<IReadOnlyList<RepositorySessionRecord>>
        ListRecoverableAsync(CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                id,
                repository_profile,
                profile_revision_sha,
                source_control_provider,
                image_reference,
                image_digest,
                status,
                message_id,
                container_name,
                container_id,
                request_payload::text,
                requested_by_subject,
                requested_by_client_id,
                requested_by_token_id,
                trace_id,
                created_at,
                published_at,
                started_at,
                completed_at,
                failure_code,
                failure_message
            FROM repository_session
            WHERE status IN (
                'starting',
                'running',
                'processing_results'
            )
            ORDER BY created_at, id;
            """,
            connection);
        var sessions = new List<RepositorySessionRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(ReadRepositorySession(reader));
        }

        return sessions;
    }

    public async Task<bool> HasActiveRepositorySessionAsync(
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM repository_session
                WHERE status IN (
                    'created',
                    'published',
                    'starting',
                    'running',
                    'processing_results'
                )
            );
            """,
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
                      ?? false);
    }

    public async Task<IReadOnlyList<string>> ListNonTerminalJiraKeysAsync(
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT jira_key
            FROM work_item_run
            WHERE status NOT IN (
                'change_request_created',
                'blocked',
                'failed'
            )
            ORDER BY jira_key;
            """,
            connection);
        var keys = new List<string>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public async Task CreateAsync(
        RepositorySessionCreation session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await InsertSessionAsync(
            connection,
            transaction,
            session,
            cancellationToken);

        foreach (var workItem in session.WorkItems)
        {
            await InsertWorkItemAsync(
                connection,
                transaction,
                session.Id,
                workItem,
                cancellationToken);
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            session.Id,
            workItemRunId: null,
            RunLedgerEventType.SessionCreated,
            "{}",
            session.CreatedAt,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CancelAsync(
        RepositorySessionCancellation cancellation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var sessionCommand = new NpgsqlCommand(
                         """
                         UPDATE repository_session
                         SET
                             status = 'cancelled',
                             completed_at = $1,
                             failure_code = 'session_cancelled',
                             failure_message = 'Cancelled by an authorized operator.'
                         WHERE id = $2 AND status = $3;
                         """,
                         connection,
                         transaction))
        {
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.TimestampTz,
                cancellation.OccurredAt);
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                cancellation.RepositorySessionId);
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                ToDatabaseValue(cancellation.ExpectedStatus));
            int affectedRows =
                await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException(
                    $"Repository session {cancellation.RepositorySessionId} " +
                    $"was not in the expected {cancellation.ExpectedStatus} status.");
            }
        }

        var cancelledWorkItemIds = new List<Guid>();
        await using (var workItemCommand = new NpgsqlCommand(
                         """
                         UPDATE work_item_run
                         SET
                             status = 'failed',
                             completed_at = $1,
                             failure_code = 'session_cancelled',
                             failure_message = 'Parent repository session was cancelled.'
                         WHERE
                             repository_session_id = $2
                             AND status NOT IN (
                                 'change_request_created',
                                 'blocked',
                                 'failed'
                             )
                         RETURNING id;
                         """,
                         connection,
                         transaction))
        {
            workItemCommand.Parameters.AddWithValue(
                NpgsqlDbType.TimestampTz,
                cancellation.OccurredAt);
            workItemCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                cancellation.RepositorySessionId);
            await using var reader =
                await workItemCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancelledWorkItemIds.Add(reader.GetGuid(0));
            }
        }

        foreach (Guid workItemId in cancelledWorkItemIds)
        {
            await InsertLedgerEntryAsync(
                connection,
                transaction,
                cancellation.RepositorySessionId,
                workItemId,
                RunLedgerEventType.ItemFailed,
                """{"failureCode":"session_cancelled"}""",
                cancellation.OccurredAt,
                cancellationToken);
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            cancellation.RepositorySessionId,
            workItemRunId: null,
            RunLedgerEventType.SessionCancelled,
            cancellation.PayloadJson,
            cancellation.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task TransitionSessionStatusAsync(
        RepositorySessionStatusTransition transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE repository_session
            SET
                status = $1,
                container_name = COALESCE($2, container_name),
                container_id = COALESCE($3, container_id),
                published_at = CASE
                    WHEN $1 = 'published' THEN $4
                    ELSE published_at
                END,
                started_at = CASE
                    WHEN $1 = 'running' THEN $4
                    ELSE started_at
                END,
                completed_at = CASE
                    WHEN $1 IN ('completed', 'failed', 'cancelled') THEN $4
                    ELSE completed_at
                END,
                failure_code = COALESCE($5, failure_code),
                failure_message = COALESCE($6, failure_message)
            WHERE id = $7 AND status = $8;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(transition.NewStatus));
        AddNullableText(command, transition.ContainerName);
        AddNullableText(command, transition.ContainerId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            transition.OccurredAt);
        AddNullableText(command, transition.FailureCode);
        AddNullableText(command, transition.FailureMessage);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            transition.RepositorySessionId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(transition.ExpectedStatus));

        var affectedRows =
            await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"Repository session {transition.RepositorySessionId} was not in " +
                $"the expected {transition.ExpectedStatus} status.");
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            transition.RepositorySessionId,
            workItemRunId: null,
            transition.EventType,
            transition.PayloadJson,
            transition.OccurredAt,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task TransitionWorkItemStatusAsync(
        WorkItemRunStatusTransition transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE work_item_run
            SET
                status = $1,
                started_at = CASE
                    WHEN $1 = 'planning' THEN $2
                    ELSE started_at
                END,
                completed_at = CASE
                    WHEN $1 IN ('change_request_created', 'blocked', 'failed')
                        THEN $2
                    ELSE completed_at
                END,
                failure_code = COALESCE($3, failure_code),
                failure_message = COALESCE($4, failure_message)
            WHERE
                id = $5
                AND repository_session_id = $6
                AND status = $7;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(transition.NewStatus));
        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            transition.OccurredAt);
        AddNullableText(command, transition.FailureCode);
        AddNullableText(command, transition.FailureMessage);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            transition.WorkItemRunId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            transition.RepositorySessionId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(transition.ExpectedStatus));

        var affectedRows =
            await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"Work-item run {transition.WorkItemRunId} was not in the " +
                $"expected {transition.ExpectedStatus} status.");
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            transition.RepositorySessionId,
            transition.WorkItemRunId,
            transition.EventType,
            transition.PayloadJson,
            transition.OccurredAt,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordValidatedResultAsync(
        AgentResultRecording result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        RepositorySessionStatus sessionStatus =
            await GetSessionStatusForUpdateAsync(
                connection,
                transaction,
                result.RepositorySessionId,
                cancellationToken);
        if (sessionStatus == RepositorySessionStatus.Running)
        {
            await using var sessionCommand = new NpgsqlCommand(
                """
                UPDATE repository_session
                SET status = 'processing_results'
                WHERE id = $1 AND status = 'running';
                """,
                connection,
                transaction);
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                result.RepositorySessionId);
            if (await sessionCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Repository session {result.RepositorySessionId} could " +
                    "not enter result processing.");
            }
        }
        else if (sessionStatus != RepositorySessionStatus.ProcessingResults)
        {
            throw new InvalidOperationException(
                $"Repository session {result.RepositorySessionId} cannot " +
                $"record results while in {sessionStatus}.");
        }

        foreach (AgentWorkItemOutcomeRecording item in result.WorkItems)
        {
            await RecordValidatedWorkItemAsync(
                connection,
                transaction,
                result.RepositorySessionId,
                result.OccurredAt,
                item,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordChangeRequestAsync(
        ChangeRequestRecording recording,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var readCommand = new NpgsqlCommand(
            """
            SELECT
                status,
                branch_name,
                candidate_commit_sha,
                change_request_id,
                change_request_number,
                change_request_url
            FROM work_item_run
            WHERE id = $1 AND repository_session_id = $2
            FOR UPDATE;
            """,
            connection,
            transaction);
        readCommand.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            recording.WorkItemRunId);
        readCommand.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            recording.RepositorySessionId);
        await using var reader =
            await readCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Work-item run {recording.WorkItemRunId} was not found.");
        }

        WorkItemRunStatus status = ParseWorkItemStatus(reader.GetString(0));
        string? branchName = GetNullableString(reader, 1);
        string? candidateSha = GetNullableString(reader, 2);
        string? changeRequestId = GetNullableString(reader, 3);
        string? changeRequestNumber = GetNullableString(reader, 4);
        string? changeRequestUrl = GetNullableString(reader, 5);
        await reader.DisposeAsync();

        RequirePersistedValue(
            recording.BranchName,
            branchName,
            "branch name",
            recording.WorkItemRunId);
        RequirePersistedValue(
            recording.CandidateCommitSha,
            candidateSha,
            "candidate SHA",
            recording.WorkItemRunId);

        if (status == WorkItemRunStatus.ChangeRequestCreated)
        {
            RequirePersistedValue(
                recording.ChangeRequest.ProviderId,
                changeRequestId,
                "change-request ID",
                recording.WorkItemRunId);
            RequirePersistedValue(
                recording.ChangeRequest.Number,
                changeRequestNumber,
                "change-request number",
                recording.WorkItemRunId);
            RequirePersistedValue(
                recording.ChangeRequest.Url.AbsoluteUri,
                changeRequestUrl,
                "change-request URL",
                recording.WorkItemRunId);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (status != WorkItemRunStatus.BranchPushed)
        {
            throw new InvalidOperationException(
                $"Work-item run {recording.WorkItemRunId} cannot record a " +
                $"change request while in {status}.");
        }

        await using (var updateCommand = new NpgsqlCommand(
                         """
                         UPDATE work_item_run
                         SET
                             status = 'change_request_created',
                             change_request_id = $1,
                             change_request_number = $2,
                             change_request_url = $3,
                             completed_at = $4
                         WHERE
                             id = $5
                             AND repository_session_id = $6
                             AND status = 'branch_pushed';
                         """,
                         connection,
                         transaction))
        {
            updateCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                recording.ChangeRequest.ProviderId);
            updateCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                recording.ChangeRequest.Number);
            updateCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                recording.ChangeRequest.Url.AbsoluteUri);
            updateCommand.Parameters.AddWithValue(
                NpgsqlDbType.TimestampTz,
                recording.OccurredAt);
            updateCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                recording.WorkItemRunId);
            updateCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                recording.RepositorySessionId);
            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Work-item run {recording.WorkItemRunId} changed while " +
                    "recording its change request.");
            }
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            recording.RepositorySessionId,
            recording.WorkItemRunId,
            RunLedgerEventType.ChangeRequestCreated,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    provider = recording.ChangeRequest.ProviderName,
                    providerId = recording.ChangeRequest.ProviderId,
                    number = recording.ChangeRequest.Number,
                    url = recording.ChangeRequest.Url,
                    revisionSha = recording.ChangeRequest.RevisionSha,
                }),
            recording.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordJiraSynchronizationWarningAsync(
        JiraSynchronizationWarningRecording recording,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentException.ThrowIfNullOrWhiteSpace(recording.Operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(recording.FailureCode);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM repository_session
                    WHERE id = $1
                ),
                $2::uuid IS NULL OR EXISTS (
                    SELECT 1
                    FROM work_item_run
                    WHERE id = $2 AND repository_session_id = $1
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            recording.RepositorySessionId);
        var workItemParameter = new NpgsqlParameter<Guid?>
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            TypedValue = recording.WorkItemRunId,
        };
        command.Parameters.Add(workItemParameter);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        _ = await reader.ReadAsync(cancellationToken);
        bool sessionExists = reader.GetBoolean(0);
        bool workItemMatches = reader.GetBoolean(1);
        await reader.DisposeAsync();
        if (!sessionExists || !workItemMatches)
        {
            throw new InvalidOperationException(
                "A Jira synchronization warning must reference an existing " +
                "session and one of its work items.");
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            recording.RepositorySessionId,
            recording.WorkItemRunId,
            RunLedgerEventType.JiraSynchronizationWarning,
            recording.PayloadJson,
            recording.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailResultProcessingAsync(
        AgentResultProcessingFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        RepositorySessionStatus status = await GetSessionStatusForUpdateAsync(
            connection,
            transaction,
            failure.RepositorySessionId,
            cancellationToken);
        if (status == RepositorySessionStatus.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (status is not RepositorySessionStatus.Running
            and not RepositorySessionStatus.ProcessingResults)
        {
            throw new InvalidOperationException(
                $"Repository session {failure.RepositorySessionId} cannot " +
                $"fail result processing while in {status}.");
        }

        await using (var sessionCommand = new NpgsqlCommand(
                         """
                         UPDATE repository_session
                         SET
                             status = 'failed',
                             completed_at = $1,
                             failure_code = $2,
                             failure_message = $3
                         WHERE id = $4;
                         """,
                         connection,
                         transaction))
        {
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.TimestampTz,
                failure.OccurredAt);
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                failure.FailureCode);
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                failure.FailureMessage);
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                failure.RepositorySessionId);
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var failedItems = new List<Guid>();
        await using (var itemCommand = new NpgsqlCommand(
                         """
                         UPDATE work_item_run
                         SET
                             status = 'failed',
                             completed_at = $1,
                             failure_code = $2,
                             failure_message = $3
                         WHERE
                             repository_session_id = $4
                             AND status NOT IN (
                                 'change_request_created',
                                 'blocked',
                                 'failed'
                             )
                         RETURNING id;
                         """,
                         connection,
                         transaction))
        {
            itemCommand.Parameters.AddWithValue(
                NpgsqlDbType.TimestampTz,
                failure.OccurredAt);
            itemCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                failure.FailureCode);
            itemCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                failure.FailureMessage);
            itemCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                failure.RepositorySessionId);
            await using var reader =
                await itemCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                failedItems.Add(reader.GetGuid(0));
            }
        }

        foreach (Guid itemId in failedItems)
        {
            await InsertLedgerEntryAsync(
                connection,
                transaction,
                failure.RepositorySessionId,
                itemId,
                RunLedgerEventType.ItemFailed,
                failure.PayloadJson,
                failure.OccurredAt,
                cancellationToken);
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            failure.RepositorySessionId,
            workItemRunId: null,
            RunLedgerEventType.SessionFailed,
            failure.PayloadJson,
            failure.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteResultProcessingAsync(
        AgentResultProcessingCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.FinalStatus is not RepositorySessionStatus.Completed
            and not RepositorySessionStatus.Failed)
        {
            throw new ArgumentException(
                "Result processing can only complete or fail a session.",
                nameof(completion));
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        RepositorySessionStatus current = await GetSessionStatusForUpdateAsync(
            connection,
            transaction,
            completion.RepositorySessionId,
            cancellationToken);
        if (current == completion.FinalStatus)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (current != RepositorySessionStatus.ProcessingResults)
        {
            throw new InvalidOperationException(
                $"Repository session {completion.RepositorySessionId} cannot " +
                $"complete result processing while in {current}.");
        }

        await using (var incompleteCommand = new NpgsqlCommand(
                         """
                         SELECT count(*)
                         FROM work_item_run
                         WHERE
                             repository_session_id = $1
                             AND status NOT IN (
                                 'change_request_created',
                                 'blocked',
                                 'failed'
                             );
                         """,
                         connection,
                         transaction))
        {
            incompleteCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                completion.RepositorySessionId);
            long incomplete = (long)(
                await incompleteCommand.ExecuteScalarAsync(cancellationToken)
                ?? -1L);
            if (incomplete != 0)
            {
                throw new InvalidOperationException(
                    $"Repository session {completion.RepositorySessionId} has " +
                    $"{incomplete} non-terminal work-item runs.");
            }
        }

        await using (var sessionCommand = new NpgsqlCommand(
                         """
                         UPDATE repository_session
                         SET
                             status = $1,
                             completed_at = $2,
                             failure_code = $3,
                             failure_message = $4
                         WHERE id = $5 AND status = 'processing_results';
                         """,
                         connection,
                         transaction))
        {
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                ToDatabaseValue(completion.FinalStatus));
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.TimestampTz,
                completion.OccurredAt);
            AddNullableText(sessionCommand, completion.FailureCode);
            AddNullableText(sessionCommand, completion.FailureMessage);
            sessionCommand.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                completion.RepositorySessionId);
            if (await sessionCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Repository session {completion.RepositorySessionId} " +
                    "changed while completing result processing.");
            }
        }

        await InsertLedgerEntryAsync(
            connection,
            transaction,
            completion.RepositorySessionId,
            workItemRunId: null,
            completion.FinalStatus == RepositorySessionStatus.Completed
                ? RunLedgerEventType.SessionCompleted
                : RunLedgerEventType.SessionFailed,
            completion.PayloadJson,
            completion.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RecordValidatedWorkItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid repositorySessionId,
        DateTimeOffset occurredAt,
        AgentWorkItemOutcomeRecording item,
        CancellationToken cancellationToken)
    {
        await using var readCommand = new NpgsqlCommand(
            """
            SELECT
                status,
                base_commit_sha,
                branch_name,
                candidate_commit_sha,
                failure_code,
                failure_message
            FROM work_item_run
            WHERE id = $1 AND repository_session_id = $2
            FOR UPDATE;
            """,
            connection,
            transaction);
        readCommand.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            item.WorkItemRunId);
        readCommand.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);
        await using var reader =
            await readCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Work-item run {item.WorkItemRunId} was not found.");
        }

        WorkItemRunStatus current = ParseWorkItemStatus(reader.GetString(0));
        string? baseSha = GetNullableString(reader, 1);
        string? branch = GetNullableString(reader, 2);
        string? candidateSha = GetNullableString(reader, 3);
        string? failureCode = GetNullableString(reader, 4);
        string? failureMessage = GetNullableString(reader, 5);
        await reader.DisposeAsync();

        if (current == item.Status
            || (item.Status == WorkItemRunStatus.BranchPushed
                && current == WorkItemRunStatus.ChangeRequestCreated))
        {
            RequirePersistedValue(
                item.BaseCommitSha,
                baseSha,
                "base SHA",
                item.WorkItemRunId);
            RequirePersistedValue(
                item.BranchName,
                branch,
                "branch name",
                item.WorkItemRunId);
            RequirePersistedValue(
                item.CandidateCommitSha,
                candidateSha,
                "candidate SHA",
                item.WorkItemRunId);
            RequirePersistedValue(
                item.FailureCode,
                failureCode,
                "failure code",
                item.WorkItemRunId);
            RequirePersistedValue(
                item.FailureMessage,
                failureMessage,
                "failure message",
                item.WorkItemRunId);
            string recordedPayload =
                await GetRecordedOutcomePayloadAsync(
                    connection,
                    transaction,
                    repositorySessionId,
                    item.WorkItemRunId,
                    item.Status,
                    cancellationToken);
            RequireEquivalentJson(
                item.LedgerPayloadJson,
                recordedPayload,
                "result payload",
                item.WorkItemRunId);
            return;
        }

        if (current is WorkItemRunStatus.ChangeRequestCreated
            or WorkItemRunStatus.Blocked
            or WorkItemRunStatus.Failed)
        {
            throw new InvalidOperationException(
                $"Work-item run {item.WorkItemRunId} already has terminal " +
                $"status {current}, not {item.Status}.");
        }

        await using var updateCommand = new NpgsqlCommand(
            """
            UPDATE work_item_run
            SET
                status = $1,
                base_commit_sha = $2,
                branch_name = $3,
                candidate_commit_sha = $4,
                plan_json = $5,
                candidate_json = $6,
                verification_json = $7,
                judgment_json = $8,
                model_usage_json = $9,
                started_at = COALESCE(started_at, $10),
                completed_at = CASE
                    WHEN $1 IN ('blocked', 'failed') THEN $10
                    ELSE completed_at
                END,
                failure_code = $11,
                failure_message = $12
            WHERE id = $13 AND repository_session_id = $14;
            """,
            connection,
            transaction);
        updateCommand.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(item.Status));
        AddNullableText(updateCommand, item.BaseCommitSha);
        AddNullableText(updateCommand, item.BranchName);
        AddNullableText(updateCommand, item.CandidateCommitSha);
        AddNullableJson(updateCommand, item.PlanJson);
        AddNullableJson(updateCommand, item.CandidateJson);
        AddNullableJson(updateCommand, item.VerificationJson);
        AddNullableJson(updateCommand, item.JudgmentJson);
        updateCommand.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            item.ModelUsageJson);
        updateCommand.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            occurredAt);
        AddNullableText(updateCommand, item.FailureCode);
        AddNullableText(updateCommand, item.FailureMessage);
        updateCommand.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            item.WorkItemRunId);
        updateCommand.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Work-item run {item.WorkItemRunId} changed while recording results.");
        }

        RunLedgerEventType eventType = item.Status switch
        {
            WorkItemRunStatus.BranchPushed => RunLedgerEventType.BranchPushed,
            WorkItemRunStatus.Blocked => RunLedgerEventType.ItemBlocked,
            WorkItemRunStatus.Failed => RunLedgerEventType.ItemFailed,
            _ => throw new InvalidOperationException(
                $"Unsupported result status {item.Status}."),
        };
        await InsertLedgerEntryAsync(
            connection,
            transaction,
            repositorySessionId,
            item.WorkItemRunId,
            eventType,
            item.LedgerPayloadJson,
            occurredAt,
            cancellationToken);
    }

    private static async Task<RepositorySessionStatus>
        GetSessionStatusForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid repositorySessionId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT status
            FROM repository_session
            WHERE id = $1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string status
            ? ParseSessionStatus(status)
            : throw new InvalidOperationException(
                $"Repository session {repositorySessionId} was not found.");
    }

    private static async Task<string> GetRecordedOutcomePayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid repositorySessionId,
        Guid workItemRunId,
        WorkItemRunStatus status,
        CancellationToken cancellationToken)
    {
        RunLedgerEventType eventType = status switch
        {
            WorkItemRunStatus.BranchPushed => RunLedgerEventType.BranchPushed,
            WorkItemRunStatus.Blocked => RunLedgerEventType.ItemBlocked,
            WorkItemRunStatus.Failed => RunLedgerEventType.ItemFailed,
            _ => throw new InvalidOperationException(
                $"Unsupported result status {status}."),
        };
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text
            FROM ledger_entry
            WHERE
                repository_session_id = $1
                AND work_item_run_id = $2
                AND entry_type = $3
            ORDER BY id
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            workItemRunId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(eventType));
        return await command.ExecuteScalarAsync(cancellationToken) as string
               ?? throw new InvalidOperationException(
                   $"Work-item run {workItemRunId} has status {status} " +
                   "without its result ledger payload.");
    }

    private static void RequirePersistedValue(
        string? expected,
        string? actual,
        string name,
        Guid workItemRunId)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Work-item run {workItemRunId} persisted {name} '{actual}', " +
                $"not '{expected}'.");
        }
    }

    private static void RequireEquivalentJson(
        string expected,
        string actual,
        string name,
        Guid workItemRunId)
    {
        using System.Text.Json.JsonDocument expectedDocument =
            System.Text.Json.JsonDocument.Parse(expected);
        using System.Text.Json.JsonDocument actualDocument =
            System.Text.Json.JsonDocument.Parse(actual);
        if (!System.Text.Json.JsonElement.DeepEquals(
                expectedDocument.RootElement,
                actualDocument.RootElement))
        {
            throw new InvalidOperationException(
                $"Work-item run {workItemRunId} persisted a different {name}.");
        }
    }

    private static async Task InsertSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RepositorySessionCreation session,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO repository_session (
                id,
                repository_profile,
                profile_revision_sha,
                source_control_provider,
                image_reference,
                image_digest,
                status,
                message_id,
                request_payload,
                requested_by_subject,
                requested_by_client_id,
                requested_by_token_id,
                trace_id,
                created_at
            )
            VALUES (
                $1,
                $2,
                $3,
                $4,
                $5,
                $6,
                $7,
                $8,
                $9,
                $10,
                $11,
                $12,
                $13,
                $14
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, session.Id);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            session.RepositoryProfile);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            session.ProfileRevisionSha);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            session.SourceControlProvider);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            session.ImageReference);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            session.ImageDigest);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(RepositorySessionStatus.Created));
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            session.MessageId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            session.RequestPayloadJson);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            session.RequestedBySubject);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            session.RequestedByClientId);
        AddNullableText(command, session.RequestedByTokenId);
        AddNullableText(command, session.TraceId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            session.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertWorkItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid repositorySessionId,
        WorkItemRunCreation workItem,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO work_item_run (
                id,
                repository_session_id,
                sequence_number,
                jira_issue_id,
                jira_key,
                jira_updated_at,
                jira_snapshot,
                status
            )
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, workItem.Id);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Integer,
            workItem.SequenceNumber);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            workItem.JiraIssueId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            workItem.JiraKey);
        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            workItem.JiraUpdatedAt);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            workItem.JiraSnapshotJson);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(WorkItemRunStatus.Queued));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLedgerEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid repositorySessionId,
        Guid? workItemRunId,
        RunLedgerEventType eventType,
        string payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger_entry (
                repository_session_id,
                work_item_run_id,
                entry_type,
                payload,
                occurred_at
            )
            VALUES ($1, $2, $3, $4, $5);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            repositorySessionId);
        var workItemParameter = new NpgsqlParameter<Guid?>
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
        };
        workItemParameter.TypedValue = workItemRunId;
        command.Parameters.Add(workItemParameter);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            ToDatabaseValue(eventType));
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            payloadJson);
        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            occurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullableText(
        NpgsqlCommand command,
        string? value)
    {
        var parameter = new NpgsqlParameter<string?>
        {
            NpgsqlDbType = NpgsqlDbType.Text,
        };
        parameter.TypedValue = value;
        command.Parameters.Add(parameter);
    }

    private static void AddNullableJson(
        NpgsqlCommand command,
        string? value)
    {
        var parameter = new NpgsqlParameter<string?>
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        };
        parameter.TypedValue = value;
        command.Parameters.Add(parameter);
    }

    private static RepositorySessionRecord ReadRepositorySession(
        NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseSessionStatus(reader.GetString(6)),
            reader.GetGuid(7),
            GetNullableString(reader, 8),
            GetNullableString(reader, 9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            GetNullableString(reader, 13),
            GetNullableString(reader, 14),
            reader.GetFieldValue<DateTimeOffset>(15),
            GetNullableDateTimeOffset(reader, 16),
            GetNullableDateTimeOffset(reader, 17),
            GetNullableDateTimeOffset(reader, 18),
            GetNullableString(reader, 19),
            GetNullableString(reader, 20));

    private static string? GetNullableString(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static RepositorySessionStatus ParseSessionStatus(string value) =>
        value switch
        {
            "created" => RepositorySessionStatus.Created,
            "published" => RepositorySessionStatus.Published,
            "starting" => RepositorySessionStatus.Starting,
            "running" => RepositorySessionStatus.Running,
            "processing_results" => RepositorySessionStatus.ProcessingResults,
            "completed" => RepositorySessionStatus.Completed,
            "failed" => RepositorySessionStatus.Failed,
            "cancelled" => RepositorySessionStatus.Cancelled,
            _ => throw new InvalidOperationException(
                $"Unknown repository session status '{value}'."),
        };

    private static WorkItemRunStatus ParseWorkItemStatus(string value) =>
        value switch
        {
            "queued" => WorkItemRunStatus.Queued,
            "planning" => WorkItemRunStatus.Planning,
            "coding" => WorkItemRunStatus.Coding,
            "verifying" => WorkItemRunStatus.Verifying,
            "judging" => WorkItemRunStatus.Judging,
            "revising" => WorkItemRunStatus.Revising,
            "accepted" => WorkItemRunStatus.Accepted,
            "branch_pushed" => WorkItemRunStatus.BranchPushed,
            "change_request_created" => WorkItemRunStatus.ChangeRequestCreated,
            "blocked" => WorkItemRunStatus.Blocked,
            "failed" => WorkItemRunStatus.Failed,
            _ => throw new InvalidOperationException(
                $"Unknown work-item run status '{value}'."),
        };

    private static string ToDatabaseValue(RepositorySessionStatus status) =>
        status switch
        {
            RepositorySessionStatus.Created => "created",
            RepositorySessionStatus.Published => "published",
            RepositorySessionStatus.Starting => "starting",
            RepositorySessionStatus.Running => "running",
            RepositorySessionStatus.ProcessingResults => "processing_results",
            RepositorySessionStatus.Completed => "completed",
            RepositorySessionStatus.Failed => "failed",
            RepositorySessionStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static string ToDatabaseValue(WorkItemRunStatus status) =>
        status switch
        {
            WorkItemRunStatus.Queued => "queued",
            WorkItemRunStatus.Planning => "planning",
            WorkItemRunStatus.Coding => "coding",
            WorkItemRunStatus.Verifying => "verifying",
            WorkItemRunStatus.Judging => "judging",
            WorkItemRunStatus.Revising => "revising",
            WorkItemRunStatus.Accepted => "accepted",
            WorkItemRunStatus.BranchPushed => "branch_pushed",
            WorkItemRunStatus.ChangeRequestCreated =>
                "change_request_created",
            WorkItemRunStatus.Blocked => "blocked",
            WorkItemRunStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static string ToDatabaseValue(RunLedgerEventType eventType) =>
        eventType switch
        {
            RunLedgerEventType.SessionCreated => "SESSION_CREATED",
            RunLedgerEventType.SessionPublished => "SESSION_PUBLISHED",
            RunLedgerEventType.SessionStarting => "SESSION_STARTING",
            RunLedgerEventType.SessionStarted => "SESSION_STARTED",
            RunLedgerEventType.SessionReattached => "SESSION_REATTACHED",
            RunLedgerEventType.SessionCompleted => "SESSION_COMPLETED",
            RunLedgerEventType.SessionFailed => "SESSION_FAILED",
            RunLedgerEventType.SessionCancelled => "SESSION_CANCELLED",
            RunLedgerEventType.ItemStarted => "ITEM_STARTED",
            RunLedgerEventType.BaseCommitResolved => "BASE_COMMIT_RESOLVED",
            RunLedgerEventType.PlanCompleted => "PLAN_COMPLETED",
            RunLedgerEventType.CodingCompleted => "CODING_COMPLETED",
            RunLedgerEventType.VerificationCompleted =>
                "VERIFICATION_COMPLETED",
            RunLedgerEventType.CandidateCommitted => "CANDIDATE_COMMITTED",
            RunLedgerEventType.JudgmentCompleted => "JUDGMENT_COMPLETED",
            RunLedgerEventType.RevisionStarted => "REVISION_STARTED",
            RunLedgerEventType.BranchPushed => "BRANCH_PUSHED",
            RunLedgerEventType.ChangeRequestCreated =>
                "CHANGE_REQUEST_CREATED",
            RunLedgerEventType.ItemBlocked => "ITEM_BLOCKED",
            RunLedgerEventType.ItemFailed => "ITEM_FAILED",
            RunLedgerEventType.JiraSynchronizationWarning =>
                "JIRA_SYNCHRONIZATION_WARNING",
            _ => throw new ArgumentOutOfRangeException(
                nameof(eventType),
                eventType,
                null),
        };
}
