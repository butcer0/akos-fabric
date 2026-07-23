using System.Text.Json;

using AkosFabric.Application.Common.Exceptions;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.Telemetry;
using AkosFabric.Domain.Ledger;
using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.Application.RepositorySessions.Services;

public sealed class RepositorySessionService : IRepositorySessionService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IRepositoryProfileProvider profileProvider;
    private readonly IJiraClient jiraClient;
    private readonly IRepositorySessionRepository repository;
    private readonly IRepositorySessionQueue queue;
    private readonly ISourceControlProviderResolver sourceControlProviders;
    private readonly AgentExecution.Interfaces.IRepositorySessionExecutor executor;
    private readonly TimeProvider timeProvider;
    private readonly IAgentControlMetrics metrics;
    private readonly IAgentControlLifecycleLogger lifecycleLogger;

    public RepositorySessionService(
        IRepositoryProfileProvider profileProvider,
        IJiraClient jiraClient,
        IRepositorySessionRepository repository,
        IRepositorySessionQueue queue,
        ISourceControlProviderResolver sourceControlProviders,
        AgentExecution.Interfaces.IRepositorySessionExecutor executor,
        TimeProvider timeProvider,
        IAgentControlMetrics? metrics = null,
        IAgentControlLifecycleLogger? lifecycleLogger = null)
    {
        this.profileProvider = profileProvider
            ?? throw new ArgumentNullException(nameof(profileProvider));
        this.jiraClient = jiraClient
            ?? throw new ArgumentNullException(nameof(jiraClient));
        this.repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
        this.queue = queue
            ?? throw new ArgumentNullException(nameof(queue));
        this.sourceControlProviders = sourceControlProviders
            ?? throw new ArgumentNullException(nameof(sourceControlProviders));
        this.executor = executor
            ?? throw new ArgumentNullException(nameof(executor));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        this.metrics = metrics ?? NullAgentControlMetrics.Instance;
        this.lifecycleLogger =
            lifecycleLogger ?? NullAgentControlLifecycleLogger.Instance;
    }

    public async Task<RepositorySessionDetails> CreateAsync(
        CreateRepositorySessionInput input,
        RepositorySessionCaller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCaller(caller);

        RepositoryProfile profile = await LoadProfileAsync(
            input.RepositoryProfile,
            cancellationToken);
        List<string> jiraKeys = ValidateJiraKeys(input.JiraKeys, profile);
        IReadOnlyList<JiraIssueSnapshot> issues = await LoadAndValidateIssuesAsync(
            profile,
            jiraKeys,
            cancellationToken);

        _ = sourceControlProviders.Resolve(profile.SourceControl.Provider);

        var now = timeProvider.GetUtcNow();
        var creation = new RepositorySessionCreation(
            Guid.NewGuid(),
            profile.Id,
            profile.ProfileRevisionSha,
            profile.SourceControl.Provider,
            profile.Image.Reference,
            profile.Image.ExpectedDigest,
            Guid.NewGuid(),
            JsonSerializer.Serialize(
                new CreateRepositorySessionInput(profile.Id, jiraKeys),
                SerializerOptions),
            caller.Subject,
            caller.ClientId,
            caller.TokenId,
            caller.TraceId,
            now,
            issues.Select(
                    (issue, index) => new WorkItemRunCreation(
                        Guid.NewGuid(),
                        index + 1,
                        issue.IssueId,
                        issue.Key,
                        issue.UpdatedAt,
                        issue.SnapshotJson))
                .ToArray());

        await repository.CreateAsync(creation, cancellationToken);
        metrics.RecordRepositorySessionCreated(
            creation.SourceControlProvider);
        lifecycleLogger.Log(
            creation.Id,
            workItemRunId: null,
            "session_created",
            creation.SourceControlProvider,
            failureCode: null);

        await PublishCreatedAsync(
            creation.Id,
            creation.MessageId,
            caller.TraceParent,
            now,
            cancellationToken);

        await AssignItemsAsync(
            profile,
            creation.Id,
            creation.WorkItems
                .Select(
                    item => (
                        item.Id,
                        item.JiraKey))
                .ToArray(),
            cancellationToken);
        return await GetAsync(creation.Id, cancellationToken);
    }

    public async Task<RepositorySessionDetails> PublishAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        RepositorySessionDetails details = await GetAsync(
            repositorySessionId,
            cancellationToken);
        EnsureStatus(
            details.Session,
            RepositorySessionStatus.Created,
            "publish");

        await PublishCreatedAsync(
            details.Session.Id,
            details.Session.MessageId,
            ResolveTraceParent(details.Session.TraceId),
            timeProvider.GetUtcNow(),
            cancellationToken);

        RepositoryProfile profile = await LoadProfileAsync(
            details.Session.RepositoryProfile,
            cancellationToken);
        await AssignItemsAsync(
            profile,
            repositorySessionId,
            details.WorkItems
                .Select(
                    item => (
                        item.Id,
                        item.JiraKey))
                .ToArray(),
            cancellationToken);
        return await GetAsync(repositorySessionId, cancellationToken);
    }

    public async Task<RepositorySessionDetails> RetryAsync(
        Guid repositorySessionId,
        RepositorySessionCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        RepositorySessionDetails source = await GetAsync(
            repositorySessionId,
            cancellationToken);
        if (source.Session.Status is not RepositorySessionStatus.Failed
            and not RepositorySessionStatus.Cancelled)
        {
            throw Conflict(
                source.Session,
                "retry",
                "Failed or Cancelled");
        }

        return await CreateAsync(
            new CreateRepositorySessionInput(
                source.Session.RepositoryProfile,
                source.WorkItems
                    .OrderBy(item => item.SequenceNumber)
                    .Select(item => item.JiraKey)
                    .ToArray()),
            caller,
            cancellationToken);
    }

    public async Task<RepositorySessionDetails> CancelAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        RepositorySessionRecord session = (await GetAsync(
            repositorySessionId,
            cancellationToken)).Session;

        if (session.Status == RepositorySessionStatus.Running)
        {
            await executor.StopAsync(repositorySessionId, cancellationToken);
        }
        else if (session.Status is not RepositorySessionStatus.Created
                 and not RepositorySessionStatus.Published)
        {
            throw Conflict(session, "cancel", "Created, Published, or Running");
        }

        await repository.CancelAsync(
            new RepositorySessionCancellation(
                repositorySessionId,
                session.Status,
                """{"reason":"operator_request"}""",
                timeProvider.GetUtcNow()),
            cancellationToken);
        lifecycleLogger.Log(
            session.Id,
            workItemRunId: null,
            "session_cancelled",
            session.SourceControlProvider,
            failureCode: null);
        return await GetAsync(repositorySessionId, cancellationToken);
    }

    public async Task<RepositorySessionDetails> GetAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        if (repositorySessionId == Guid.Empty)
        {
            throw new RepositorySessionValidationException(
                "A repository-session ID is required.");
        }

        RepositorySessionRecord session =
            await repository.FindAsync(repositorySessionId, cancellationToken)
            ?? throw new RepositorySessionNotFoundException(repositorySessionId);
        IReadOnlyList<WorkItemRunRecord> items =
            await repository.ListWorkItemsAsync(
                repositorySessionId,
                cancellationToken);
        return new RepositorySessionDetails(session, items);
    }

    public async Task<IReadOnlyList<WorkItemRunRecord>> ListItemsAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        _ = await repository.FindAsync(repositorySessionId, cancellationToken)
            ?? throw new RepositorySessionNotFoundException(repositorySessionId);
        return await repository.ListWorkItemsAsync(
            repositorySessionId,
            cancellationToken);
    }

    public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new RepositorySessionValidationException(
                "The session list limit must be between 1 and 200.");
        }

        return repository.ListAsync(limit, cancellationToken);
    }

    private async Task PublishCreatedAsync(
        Guid repositorySessionId,
        Guid messageId,
        string traceParent,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await queue.PublishAsync(
                new RepositorySessionRequestedV1(
                    1,
                    messageId,
                    repositorySessionId,
                    traceParent,
                    requestedAt),
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                  || !cancellationToken.IsCancellationRequested)
        {
            throw new RepositorySessionPublicationException(
                repositorySessionId,
                exception);
        }

        await repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                repositorySessionId,
                RepositorySessionStatus.Created,
                RepositorySessionStatus.Published,
                RunLedgerEventType.SessionPublished,
                "{}",
                timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private async Task<RepositoryProfile> LoadProfileAsync(
        string profileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new RepositorySessionValidationException(
                "A repository profile is required.");
        }

        return await profileProvider.FindAsync(
                   profileName.Trim(),
                   cancellationToken)
               ?? throw new RepositorySessionValidationException(
                   $"Repository profile '{profileName}' was not found.");
    }

    private async Task<IReadOnlyList<JiraIssueSnapshot>>
        LoadAndValidateIssuesAsync(
            RepositoryProfile profile,
            List<string> jiraKeys,
            CancellationToken cancellationToken)
    {
        var issues = new List<JiraIssueSnapshot>(jiraKeys.Count);
        foreach (string jiraKey in jiraKeys)
        {
            JiraIssueSnapshot issue =
                await jiraClient.FindIssueAsync(
                    profile.Jira,
                    jiraKey,
                    cancellationToken)
                ?? throw new RepositorySessionValidationException(
                    $"Jira issue '{jiraKey}' was not found.");
            ValidateIssue(profile, jiraKey, issue);
            issues.Add(issue);
        }

        return issues;
    }

    private async Task AssignItemsAsync(
        RepositoryProfile profile,
        Guid repositorySessionId,
        IReadOnlyList<(Guid WorkItemRunId, string JiraKey)> items,
        CancellationToken cancellationToken)
    {
        foreach ((Guid workItemRunId, string jiraKey) in items)
        {
            await AssignIssueAsync(
                profile,
                repositorySessionId,
                workItemRunId,
                jiraKey,
                cancellationToken);
        }
    }

    private async Task AssignIssueAsync(
        RepositoryProfile profile,
        Guid repositorySessionId,
        Guid workItemRunId,
        string jiraKey,
        CancellationToken cancellationToken)
    {
        try
        {
            JiraTransitionResult result =
                await jiraClient.TransitionIssueAsync(
                    profile.Jira,
                    jiraKey,
                    JiraWorkflowTarget.Assigned,
                    cancellationToken);
            if (result.Outcome != JiraTransitionOutcome.Applied)
            {
                await RecordJiraWarningAsync(
                    repositorySessionId,
                    workItemRunId,
                    jiraKey,
                    "transition_assigned",
                    "jira_transition_unavailable",
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await RecordJiraWarningAsync(
                repositorySessionId,
                workItemRunId,
                jiraKey,
                "transition_assigned",
                "jira_transition_failed",
                cancellationToken);
        }

        try
        {
            await jiraClient.AddCommentAsync(
                profile.Jira,
                jiraKey,
                repositorySessionId.ToString("D"),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await RecordJiraWarningAsync(
                repositorySessionId,
                workItemRunId,
                jiraKey,
                "comment_assignment",
                "jira_comment_failed",
                cancellationToken);
        }
    }

    private Task RecordJiraWarningAsync(
        Guid repositorySessionId,
        Guid workItemRunId,
        string jiraKey,
        string operation,
        string failureCode,
        CancellationToken cancellationToken) =>
        repository.RecordJiraSynchronizationWarningAsync(
            new JiraSynchronizationWarningRecording(
                repositorySessionId,
                workItemRunId,
                operation,
                failureCode,
                JsonSerializer.Serialize(
                    new
                    {
                        integration = "jira",
                        operation,
                        failureCode,
                        jiraKey,
                    }),
                timeProvider.GetUtcNow()),
            cancellationToken);

    private static List<string> ValidateJiraKeys(
        IReadOnlyList<string>? jiraKeys,
        RepositoryProfile profile)
    {
        if (jiraKeys is null || jiraKeys.Count == 0)
        {
            throw new RepositorySessionValidationException(
                "At least one Jira issue key is required.");
        }

        if (jiraKeys.Count > profile.Session.MaxItems)
        {
            throw new RepositorySessionValidationException(
                $"The repository profile permits at most " +
                $"{profile.Session.MaxItems} Jira issues per session.");
        }

        var result = new List<string>(jiraKeys.Count);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? jiraKey in jiraKeys)
        {
            if (string.IsNullOrWhiteSpace(jiraKey))
            {
                throw new RepositorySessionValidationException(
                    "Jira issue keys must be non-empty.");
            }

            string normalized = jiraKey.Trim().ToUpperInvariant();
            if (!unique.Add(normalized))
            {
                throw new RepositorySessionValidationException(
                    $"Jira issue key '{normalized}' was supplied more than once.");
            }

            result.Add(normalized);
        }

        return result;
    }

    private static void ValidateIssue(
        RepositoryProfile profile,
        string requestedKey,
        JiraIssueSnapshot issue)
    {
        if (!string.Equals(
                issue.Key,
                requestedKey,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RepositorySessionValidationException(
                $"Jira returned issue '{issue.Key}' for requested key " +
                $"'{requestedKey}'.");
        }

        if (!profile.Jira.EligibleIssueTypes.Contains(
                issue.IssueType,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new RepositorySessionValidationException(
                $"Jira issue '{issue.Key}' has ineligible type " +
                $"'{issue.IssueType}'.");
        }

        if (!string.Equals(
                issue.Status,
                profile.Jira.Workflow.EligibleStatus,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RepositorySessionValidationException(
                $"Jira issue '{issue.Key}' has status '{issue.Status}', not " +
                $"the required '{profile.Jira.Workflow.EligibleStatus}'.");
        }

        if (string.IsNullOrWhiteSpace(issue.Description))
        {
            throw new RepositorySessionValidationException(
                $"Jira issue '{issue.Key}' has no description.");
        }
    }

    private static void ValidateCaller(RepositorySessionCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(caller.Subject)
            || string.IsNullOrWhiteSpace(caller.ClientId)
            || string.IsNullOrWhiteSpace(caller.TraceParent))
        {
            throw new RepositorySessionValidationException(
                "Validated caller subject, client identity, and trace context " +
                "are required.");
        }
    }

    private static void EnsureStatus(
        RepositorySessionRecord session,
        RepositorySessionStatus expectedStatus,
        string operation)
    {
        if (session.Status != expectedStatus)
        {
            throw Conflict(session, operation, expectedStatus.ToString());
        }
    }

    private static RepositorySessionConflictException Conflict(
        RepositorySessionRecord session,
        string operation,
        string allowedStatuses) =>
        new(
            $"Repository session '{session.Id}' cannot {operation} while in " +
            $"status '{session.Status}'. Allowed status: {allowedStatuses}.");

    private static string ResolveTraceParent(string? traceId)
    {
        string normalizedTraceId =
            traceId is not null
            && traceId.Length == 32
            && traceId.All(Uri.IsHexDigit)
                ? traceId.ToLowerInvariant()
                : Guid.NewGuid().ToString("N");
        string spanId = Guid.NewGuid().ToString("N")[..16];
        return $"00-{normalizedTraceId}-{spanId}-01";
    }
}
