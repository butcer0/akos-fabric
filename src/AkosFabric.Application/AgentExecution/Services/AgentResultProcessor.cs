using System.Globalization;
using System.Text;
using System.Text.Json;
using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Application.Telemetry;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Domain.WorkItems;

namespace AkosFabric.Application.AgentExecution.Services;

public sealed class AgentResultProcessor : IAgentResultProcessor
{
    private const string InvalidResultFailureCode = "invalid_agent_result";

    private readonly IAgentSessionArtifactReader artifactReader;
    private readonly IRepositorySessionRepository repository;
    private readonly IRepositoryProfileProvider profileProvider;
    private readonly ISourceControlProviderResolver sourceControlProviders;
    private readonly IJiraClient jiraClient;
    private readonly TimeProvider timeProvider;
    private readonly IAgentControlMetrics metrics;
    private readonly IAgentControlLifecycleLogger lifecycleLogger;

    public AgentResultProcessor(
        IAgentSessionArtifactReader artifactReader,
        IRepositorySessionRepository repository,
        IRepositoryProfileProvider profileProvider,
        ISourceControlProviderResolver sourceControlProviders,
        IJiraClient jiraClient,
        TimeProvider timeProvider,
        IAgentControlMetrics? metrics = null,
        IAgentControlLifecycleLogger? lifecycleLogger = null)
    {
        this.artifactReader = artifactReader
            ?? throw new ArgumentNullException(nameof(artifactReader));
        this.repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
        this.profileProvider = profileProvider
            ?? throw new ArgumentNullException(nameof(profileProvider));
        this.sourceControlProviders = sourceControlProviders
            ?? throw new ArgumentNullException(nameof(sourceControlProviders));
        this.jiraClient = jiraClient
            ?? throw new ArgumentNullException(nameof(jiraClient));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        this.metrics = metrics ?? NullAgentControlMetrics.Instance;
        this.lifecycleLogger =
            lifecycleLogger ?? NullAgentControlLifecycleLogger.Instance;
    }

    public async Task ProcessAsync(
        RepositorySessionContainer container,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.State != RepositorySessionContainerState.Exited)
        {
            throw new InvalidOperationException(
                $"Session artifacts can only be read after container exit; " +
                $"container '{container.ContainerName}' is {container.State}.");
        }

        await ProcessCoreAsync(
            container.RepositorySessionId,
            container,
            cancellationToken);
    }

    public Task ProcessRecoveredAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        if (repositorySessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A repository-session ID is required.",
                nameof(repositorySessionId));
        }

        return ProcessCoreAsync(
            repositorySessionId,
            container: null,
            cancellationToken);
    }

    private async Task ProcessCoreAsync(
        Guid repositorySessionId,
        RepositorySessionContainer? container,
        CancellationToken cancellationToken)
    {
        RepositorySessionRecord session =
            await repository.FindAsync(
                repositorySessionId,
                cancellationToken)
            ?? throw new AgentResultValidationException(
                $"Repository session '{repositorySessionId}' was not found.");

        if (session.Status is RepositorySessionStatus.Failed
            or RepositorySessionStatus.Cancelled)
        {
            return;
        }

        bool synchronizationReplay =
            session.Status == RepositorySessionStatus.Completed;
        if (session.Status is not RepositorySessionStatus.Running
            and not RepositorySessionStatus.ProcessingResults
            and not RepositorySessionStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Repository session '{session.Id}' cannot process results " +
                $"while in status '{session.Status}'.");
        }

        IReadOnlyList<WorkItemRunRecord> persistedItems =
            await repository.ListWorkItemsAsync(session.Id, cancellationToken);

        AgentSessionArtifactsV1 artifacts;
        RepositoryProfile profile;
        try
        {
            artifacts = await artifactReader.ReadAsync(
                session.Id,
                cancellationToken);
            profile = await profileProvider.FindAsync(
                          session.RepositoryProfile,
                          cancellationToken)
                      ?? throw new AgentResultValidationException(
                          $"Repository profile '{session.RepositoryProfile}' was not found.");
            ValidateArtifacts(
                container,
                session,
                persistedItems,
                profile,
                artifacts);
        }
        catch (AgentResultValidationException exception)
        {
            await FailResultProcessingAsync(
                session,
                new AgentResultProcessingFailure(
                    session.Id,
                    InvalidResultFailureCode,
                    exception.Message,
                    JsonSerializer.Serialize(
                        new
                        {
                            failureCode = InvalidResultFailureCode,
                            exception.Message,
                        }),
                    timeProvider.GetUtcNow()),
                cancellationToken);
            return;
        }

        if (!synchronizationReplay)
        {
            bool firstResultRecording =
                session.Status == RepositorySessionStatus.Running;
            await repository.RecordValidatedResultAsync(
                CreateResultRecording(artifacts),
                cancellationToken);
            if (firstResultRecording)
            {
                RecordValidatedResultMetrics(
                    session.SourceControlProvider,
                    artifacts.Result);
                foreach (AgentWorkItemResultV1 item
                         in artifacts.Result.Items)
                {
                    lifecycleLogger.Log(
                        session.Id,
                        item.WorkItemRunId,
                        "work_item_result_recorded",
                        session.SourceControlProvider,
                        item.FailureCode);
                }
            }
        }

        persistedItems = await repository.ListWorkItemsAsync(
            session.Id,
            cancellationToken);
        var persistedById = persistedItems.ToDictionary(item => item.Id);
        ISourceControlProvider provider =
            sourceControlProviders.Resolve(artifacts.Result.Repository.Provider);
        var sourceRepository = new SourceRepositoryReference(
            artifacts.Result.Repository.Provider,
            artifacts.Result.Repository.ProviderRepositoryId,
            artifacts.Result.Repository.CloneUrl);

        AgentWorkItemResultV1[] pushedItems = artifacts.Result.Items
            .Where(item => item.Status == "branch_pushed")
            .ToArray();

        foreach (AgentWorkItemResultV1 item in pushedItems)
        {
            string remoteHead = await provider.GetBranchHeadShaAsync(
                sourceRepository,
                item.BranchName!,
                cancellationToken);
            if (!string.Equals(
                    remoteHead,
                    item.CandidateCommitSha,
                    StringComparison.Ordinal))
            {
                await FailResultProcessingAsync(
                    session,
                    new AgentResultProcessingFailure(
                        session.Id,
                        "remote_branch_sha_mismatch",
                        $"Remote branch '{item.BranchName}' resolved to " +
                        $"'{remoteHead}', not judged candidate " +
                        $"'{item.CandidateCommitSha}'.",
                        JsonSerializer.Serialize(
                            new
                            {
                                item.WorkItemRunId,
                                item.BranchName,
                                expectedSha = item.CandidateCommitSha,
                                actualSha = remoteHead,
                            }),
                        timeProvider.GetUtcNow()),
                    cancellationToken);
                return;
            }
        }

        foreach (AgentWorkItemResultV1 item in pushedItems)
        {
            WorkItemRunRecord persisted = persistedById[item.WorkItemRunId];
            ChangeRequestReference changeRequest =
                await ResolveChangeRequestAsync(
                    session,
                    profile,
                    sourceRepository,
                    provider,
                    persisted,
                    item,
                    cancellationToken);

            await SynchronizeJiraAsync(
                session.Id,
                item.WorkItemRunId,
                profile,
                item.JiraKey,
                changeRequest,
                cancellationToken);
        }

        if (!synchronizationReplay)
        {
            bool failed = artifacts.Result.Status == "failed";
            DateTimeOffset completedAt = timeProvider.GetUtcNow();
            await repository.CompleteResultProcessingAsync(
                new AgentResultProcessingCompletion(
                    session.Id,
                    failed
                        ? RepositorySessionStatus.Failed
                        : RepositorySessionStatus.Completed,
                    failed ? artifacts.Result.FailureCode : null,
                    failed ? artifacts.Result.FailureMessage : null,
                    artifacts.ResultJson,
                    completedAt),
                cancellationToken);
            metrics.RecordRepositorySessionDuration(
                session.SourceControlProvider,
                failed ? "failed" : "completed",
                completedAt -
                (session.StartedAt ?? artifacts.Result.StartedAt));
            lifecycleLogger.Log(
                session.Id,
                workItemRunId: null,
                failed ? "session_failed" : "session_completed",
                session.SourceControlProvider,
                failed ? artifacts.Result.FailureCode : null);
        }
    }

    private async Task<ChangeRequestReference> ResolveChangeRequestAsync(
        RepositorySessionRecord session,
        RepositoryProfile profile,
        SourceRepositoryReference sourceRepository,
        ISourceControlProvider provider,
        WorkItemRunRecord persisted,
        AgentWorkItemResultV1 item,
        CancellationToken cancellationToken)
    {
        ChangeRequestReference changeRequest;
        if (persisted.Status == WorkItemRunStatus.ChangeRequestCreated)
        {
            if (persisted.ChangeRequestId is null
                || persisted.ChangeRequestNumber is null
                || persisted.ChangeRequestUrl is null)
            {
                throw new InvalidOperationException(
                    $"Work-item run '{persisted.Id}' has change-request status " +
                    "without complete neutral change-request fields.");
            }

            changeRequest = new ChangeRequestReference(
                provider.ProviderName,
                persisted.ChangeRequestId,
                persisted.ChangeRequestNumber,
                new Uri(persisted.ChangeRequestUrl, UriKind.Absolute),
                item.CandidateCommitSha!);
        }
        else
        {
            ChangeRequestReference? existing =
                await provider.FindOpenChangeRequestAsync(
                sourceRepository,
                item.BranchName!,
                cancellationToken);
            bool created = existing is null;
            changeRequest = existing
                ?? await provider.CreateChangeRequestAsync(
                    new CreateChangeRequest(
                        sourceRepository,
                        item.BranchName!,
                        profile.Repository.DefaultBranch,
                        CreateTitle(item),
                        CreateBody(session, item),
                        IsDraft: true),
                    cancellationToken);

            ValidateChangeRequest(
                provider.ProviderName,
                item.CandidateCommitSha!,
                changeRequest);
            await repository.RecordChangeRequestAsync(
                new ChangeRequestRecording(
                    session.Id,
                    item.WorkItemRunId,
                    item.BranchName!,
                    item.CandidateCommitSha!,
                    changeRequest,
                    timeProvider.GetUtcNow()),
                cancellationToken);
            if (created)
            {
                metrics.RecordChangeRequestCreated(
                    provider.ProviderName);
                lifecycleLogger.Log(
                    session.Id,
                    item.WorkItemRunId,
                    "change_request_created",
                    provider.ProviderName,
                    failureCode: null);
            }
        }

        ValidateChangeRequest(
            provider.ProviderName,
            item.CandidateCommitSha!,
            changeRequest);
        return changeRequest;
    }

    private void RecordValidatedResultMetrics(
        string sourceControlProvider,
        AgentSessionResultV1 result)
    {
        foreach (AgentWorkItemResultV1 item in result.Items)
        {
            metrics.RecordWorkItem(
                sourceControlProvider,
                item.Status);
            RecordRoleModelUsage(
                item.ModelUsage,
                "planner",
                item.ModelUsage.Planner);
            RecordRoleModelUsage(
                item.ModelUsage,
                "coder",
                item.ModelUsage.Coder);
            RecordRoleModelUsage(
                item.ModelUsage,
                "judge",
                item.ModelUsage.Judge);

            if (item.Verification is JsonElement verification
                && !verification.GetProperty("passed").GetBoolean())
            {
                metrics.RecordVerificationFailure(
                    sourceControlProvider);
            }

            if (item.Judgment is JsonElement judgment)
            {
                metrics.RecordJudgeDisposition(
                    sourceControlProvider,
                    judgment.GetProperty("disposition").GetString()
                    ?? throw new AgentResultValidationException(
                        "Judgment disposition is required."));
            }
        }
    }

    private void RecordRoleModelUsage(
        AgentModelUsageV1 modelUsage,
        string role,
        JsonElement usage) =>
        metrics.RecordModelUsage(
            modelUsage.Provider,
            modelUsage.ModelId,
            role,
            usage.GetProperty("modelCalls").GetInt64(),
            usage.GetProperty("inputTokens").GetInt64(),
            usage.GetProperty("outputTokens").GetInt64(),
            usage.GetProperty("estimatedCostUsd").GetDecimal());

    private async Task SynchronizeJiraAsync(
        Guid repositorySessionId,
        Guid workItemRunId,
        RepositoryProfile profile,
        string jiraKey,
        ChangeRequestReference changeRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            JiraTransitionResult transition =
                await jiraClient.TransitionIssueAsync(
                    profile.Jira,
                    jiraKey,
                    JiraWorkflowTarget.Review,
                    cancellationToken);
            if (transition.Outcome != JiraTransitionOutcome.Applied)
            {
                await RecordJiraWarningAsync(
                    repositorySessionId,
                    workItemRunId,
                    jiraKey,
                    "transition_review",
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
                "transition_review",
                "jira_transition_failed",
                cancellationToken);
        }

        try
        {
            await jiraClient.AddCommentAsync(
                profile.Jira,
                jiraKey,
                $"Agent change request: {changeRequest.Url}",
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
                "comment_change_request",
                "jira_comment_failed",
                cancellationToken);
        }
    }

    private async Task FailResultProcessingAsync(
        RepositorySessionRecord session,
        AgentResultProcessingFailure failure,
        CancellationToken cancellationToken)
    {
        await repository.FailResultProcessingAsync(
            failure,
            cancellationToken);
        if (session.StartedAt is DateTimeOffset startedAt
            && failure.OccurredAt >= startedAt)
        {
            metrics.RecordRepositorySessionDuration(
                session.SourceControlProvider,
                "failed",
                failure.OccurredAt - startedAt);
        }

        lifecycleLogger.Log(
            session.Id,
            workItemRunId: null,
            "session_failed",
            session.SourceControlProvider,
            failure.FailureCode);
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

    private static AgentResultRecording CreateResultRecording(
        AgentSessionArtifactsV1 artifacts)
    {
        var outcomes = artifacts.Result.Items.Select(
                item => new AgentWorkItemOutcomeRecording(
                    item.WorkItemRunId,
                    item.Status switch
                    {
                        "branch_pushed" => WorkItemRunStatus.BranchPushed,
                        "blocked" => WorkItemRunStatus.Blocked,
                        "failed" => WorkItemRunStatus.Failed,
                        _ => throw new AgentResultValidationException(
                            $"Unknown work-item result status '{item.Status}'."),
                    },
                    item.BaseCommitSha,
                    item.BranchName,
                    item.CandidateCommitSha,
                    GetRawText(item.Plan),
                    GetRawText(item.Candidate),
                    GetRawText(item.Verification),
                    GetRawText(item.Judgment),
                    GetRequiredPropertyRawText(
                        artifacts.ItemResultJson[item.WorkItemRunId],
                        "modelUsage"),
                    item.FailureCode,
                    item.FailureMessage,
                    artifacts.ItemResultJson[item.WorkItemRunId]))
            .ToArray();
        return new AgentResultRecording(
            artifacts.Result.RepositorySessionId,
            artifacts.Result.CompletedAt,
            outcomes);
    }

    private static void ValidateArtifacts(
        RepositorySessionContainer? container,
        RepositorySessionRecord session,
        IReadOnlyList<WorkItemRunRecord> persistedItems,
        RepositoryProfile profile,
        AgentSessionArtifactsV1 artifacts)
    {
        AgentSessionManifestV1 manifest = artifacts.Manifest;
        AgentSessionResultV1 result = artifacts.Result;

        if (container is not null)
        {
            RequireEqual(session.Id, container.RepositorySessionId, "container session ID");
            RequireEqual(session.RepositoryProfile, container.RepositoryProfile, "container repository profile");
            RequireEqual(session.ContainerName, container.ContainerName, "container name");
            RequireEqual(session.ContainerId, container.ContainerId, "container ID");
        }
        RequireEqual(session.Id, manifest.RepositorySessionId, "manifest session ID");
        RequireEqual(session.Id, result.RepositorySessionId, "result session ID");
        RequireEqual(session.RepositoryProfile, manifest.RepositoryProfile, "repository profile");
        RequireEqual(session.ProfileRevisionSha, manifest.ProfileRevisionSha, "profile revision");
        RequireEqual(session.ProfileRevisionSha, profile.ProfileRevisionSha, "loaded profile revision");
        RequireEqual(session.ImageDigest, manifest.ImageDigest, "image digest");
        RequireEqual(session.SourceControlProvider, manifest.SourceControl.Provider, "source-control provider");
        RequireEqual(profile.SourceControl.Provider, manifest.SourceControl.Provider, "profile source-control provider");
        RequireEqual(profile.SourceControl.BaseUrl, manifest.SourceControl.BaseUrl, "manifest source-control base URL");
        RequireEqual(profile.Repository.ProviderRepositoryId, manifest.MainRepository.ProviderRepositoryId, "manifest repository ID");
        RequireEqual(profile.Repository.CloneUrl, manifest.MainRepository.CloneUrl, "manifest clone URL");
        RequireEqual(profile.Repository.DefaultBranch, manifest.MainRepository.DefaultBranch, "manifest default branch");
        RequireEqual(profile.Repository.CloneStrategy, manifest.MainRepository.CloneStrategy, "manifest clone strategy");
        RequireEqual(profile.Repository.GitLfs, manifest.MainRepository.GitLfs, "manifest Git LFS setting");
        RequireEqual(profile.Repository.Submodules, manifest.MainRepository.Submodules, "manifest submodule setting");
        RequireEqual(profile.Llm.Provider, manifest.Llm.Provider, "manifest LLM provider");
        RequireEqual(profile.Llm.ModelId, manifest.Llm.ModelId, "manifest LLM model");
        RequireEqual(profile.Llm.OpenHandsModel, manifest.Llm.OpenHandsModel, "manifest OpenHands model");
        RequireEqual(manifest.SourceControl.Provider, result.Repository.Provider, "result source-control provider");
        RequireEqual(manifest.MainRepository.ProviderRepositoryId, result.Repository.ProviderRepositoryId, "result repository ID");
        RequireEqual(manifest.MainRepository.CloneUrl, result.Repository.CloneUrl, "result clone URL");
        RequireEqual(manifest.Llm.Provider, result.Llm.Provider, "result LLM provider");
        RequireEqual(manifest.Llm.ModelId, result.Llm.ModelId, "result LLM model");
        if (container is not null
            && result.Status == "completed"
            && container.ExitCode != 0)
        {
            throw new AgentResultValidationException(
                $"Container exited with code {container.ExitCode} but result " +
                "claims completed status.");
        }
        RequireEqual(
            profile.Session.ContinueAfterItemFailure,
            manifest.SessionBehavior.ContinueAfterItemFailure,
            "continue-after-failure setting");
        RequireEqual(
            checked(profile.Session.MaxDurationMinutes * 60),
            manifest.Limits.SessionDeadlineSeconds,
            "session deadline");
        RequireEqual(
            profile.Session.MaxItems,
            manifest.Limits.MaximumItems,
            "maximum items");
        RequireEqual(
            profile.Item.MaximumCostUsd,
            manifest.Limits.MaximumCostUsdPerItem,
            "maximum item cost");
        RequireEqual(
            profile.Item.MaximumChangedFiles,
            manifest.Limits.MaximumChangedFiles,
            "maximum changed files");
        RequireEqual(
            profile.Item.MaximumDiffLines,
            manifest.Limits.MaximumDiffLines,
            "maximum diff lines");
        RequireEqual(
            profile.Item.MaximumCoderConversations,
            manifest.Limits.MaximumCoderConversations,
            "maximum coder conversations");
        RequireEqual(
            profile.Item.MaximumModelCallsPerRole,
            manifest.Limits.MaximumModelCallsPerRole,
            "maximum model calls per role");

        ValidateSupplementalRepositories(
            profile.SupplementalRepositories,
            manifest.SupplementalRepositories);
        if (result.CompletedAt < result.StartedAt)
        {
            throw new AgentResultValidationException(
                "Result completedAt precedes startedAt.");
        }

        ValidateManifestItems(persistedItems, manifest.WorkItems);
        ValidateResultItems(
            persistedItems,
            manifest.WorkItems,
            result.Items,
            manifest.Llm,
            manifest.Limits);
    }

    private static void ValidateManifestItems(
        IReadOnlyList<WorkItemRunRecord> persistedItems,
        IReadOnlyList<AgentWorkItemManifestV1> manifestItems)
    {
        EnsureUnique(
            manifestItems.Select(item => item.WorkItemRunId),
            "manifest work-item ID");
        EnsureUnique(
            manifestItems.Select(item => item.JiraKey),
            "manifest Jira key");
        EnsureUnique(
            manifestItems.Select(item => item.SequenceNumber),
            "manifest sequence number");

        if (manifestItems.Count != persistedItems.Count)
        {
            throw new AgentResultValidationException(
                "Manifest work-item count does not match the session ledger.");
        }

        var persistedById = persistedItems.ToDictionary(item => item.Id);
        foreach (AgentWorkItemManifestV1 item in manifestItems)
        {
            if (!persistedById.TryGetValue(item.WorkItemRunId, out var persisted))
            {
                throw new AgentResultValidationException(
                    $"Manifest work-item '{item.WorkItemRunId}' does not belong to the session.");
            }

            RequireEqual(persisted.JiraKey, item.JiraKey, "manifest Jira key");
            RequireEqual(persisted.SequenceNumber, item.SequenceNumber, "manifest sequence number");
            RequireEqual(persisted.JiraUpdatedAt, item.JiraUpdatedAt, "manifest Jira revision");
            using JsonDocument persistedSnapshot =
                JsonDocument.Parse(persisted.JiraSnapshotJson);
            if (!JsonElement.DeepEquals(
                    persistedSnapshot.RootElement,
                    item.JiraSnapshot))
            {
                throw new AgentResultValidationException(
                    $"Manifest Jira snapshot for work-item '{item.WorkItemRunId}' " +
                    "does not match the session ledger.");
            }
        }
    }

    private static void ValidateResultItems(
        IReadOnlyList<WorkItemRunRecord> persistedItems,
        IReadOnlyList<AgentWorkItemManifestV1> manifestItems,
        IReadOnlyList<AgentWorkItemResultV1> resultItems,
        AgentLlmV1 manifestLlm,
        AgentSessionLimitsV1 limits)
    {
        EnsureUnique(
            resultItems.Select(item => item.WorkItemRunId),
            "result work-item ID");
        EnsureUnique(
            resultItems.Select(item => item.JiraKey),
            "result Jira key");

        if (resultItems.Count != manifestItems.Count)
        {
            throw new AgentResultValidationException(
                "Result work-item count does not match the manifest.");
        }

        var persistedById = persistedItems.ToDictionary(item => item.Id);
        var manifestById = manifestItems.ToDictionary(item => item.WorkItemRunId);
        var branches = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentWorkItemResultV1 item in resultItems)
        {
            if (!manifestById.TryGetValue(item.WorkItemRunId, out var manifest)
                || !persistedById.TryGetValue(item.WorkItemRunId, out var persisted))
            {
                throw new AgentResultValidationException(
                    $"Result work-item '{item.WorkItemRunId}' does not belong to the session.");
            }

            RequireEqual(manifest.JiraKey, item.JiraKey, "result Jira key");
            RequireEqual(persisted.JiraKey, item.JiraKey, "ledger Jira key");
            RequireEqual(
                manifestLlm.Provider,
                item.ModelUsage.Provider,
                "item model-usage provider");
            RequireEqual(
                manifestLlm.ModelId,
                item.ModelUsage.ModelId,
                "item model-usage model");
            if (item.ChangedFiles.Count > limits.MaximumChangedFiles)
            {
                throw new AgentResultValidationException(
                    $"Work-item '{item.WorkItemRunId}' reports " +
                    $"{item.ChangedFiles.Count} changed files; the limit is " +
                    $"{limits.MaximumChangedFiles}.");
            }

            if (item.ModelUsage.TotalEstimatedCostUsd >
                limits.MaximumCostUsdPerItem)
            {
                throw new AgentResultValidationException(
                    $"Work-item '{item.WorkItemRunId}' reports model cost " +
                    $"{item.ModelUsage.TotalEstimatedCostUsd}; the limit is " +
                    $"{limits.MaximumCostUsdPerItem}.");
            }

            foreach (JsonElement usage in new[]
                     {
                         item.ModelUsage.Planner,
                         item.ModelUsage.Coder,
                         item.ModelUsage.Judge,
                     })
            {
                int calls = usage.GetProperty("modelCalls").GetInt32();
                if (calls > limits.MaximumModelCallsPerRole)
                {
                    throw new AgentResultValidationException(
                        $"Work-item '{item.WorkItemRunId}' reports {calls} " +
                        $"model calls for a role; the limit is " +
                        $"{limits.MaximumModelCallsPerRole}.");
                }
            }

            if (item.Status != "branch_pushed")
            {
                continue;
            }

            if (!branches.Add(item.BranchName!))
            {
                throw new AgentResultValidationException(
                    $"Branch '{item.BranchName}' appears more than once.");
            }

            JsonElement judgment = item.Judgment!.Value;
            RequireEqual(
                item.CandidateCommitSha!,
                judgment.GetProperty("candidate_sha").GetString()!,
                "judgment candidate SHA");
            RequireEqual(
                "accept",
                judgment.GetProperty("disposition").GetString()!,
                "judgment disposition");
            if (!item.Verification!.Value.GetProperty("passed").GetBoolean())
            {
                throw new AgentResultValidationException(
                    $"Pushed work-item '{item.WorkItemRunId}' did not pass verification.");
            }
        }
    }

    private static void ValidateSupplementalRepositories(
        IReadOnlyList<SupplementalRepositoryDefinition> expected,
        IReadOnlyList<AgentSupplementalRepositoryV1> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new AgentResultValidationException(
                "Manifest supplemental-repository count does not match the profile.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            SupplementalRepositoryDefinition expectedRepository = expected[index];
            AgentSupplementalRepositoryV1 actualRepository = actual[index];
            RequireEqual(expectedRepository.ProviderRepositoryId, actualRepository.ProviderRepositoryId, $"supplemental repository {index} ID");
            RequireEqual(expectedRepository.CloneUrl, actualRepository.CloneUrl, $"supplemental repository {index} clone URL");
            RequireEqual(expectedRepository.DefaultBranch, actualRepository.DefaultBranch, $"supplemental repository {index} default branch");
            RequireEqual(expectedRepository.CloneStrategy, actualRepository.CloneStrategy, $"supplemental repository {index} clone strategy");
            RequireEqual(expectedRepository.GitLfs, actualRepository.GitLfs, $"supplemental repository {index} Git LFS setting");
            RequireEqual(expectedRepository.Submodules, actualRepository.Submodules, $"supplemental repository {index} submodule setting");
            RequireEqual(expectedRepository.Writable, actualRepository.Writable, $"supplemental repository {index} writable setting");
        }
    }

    private static void ValidateChangeRequest(
        string expectedProvider,
        string expectedRevision,
        ChangeRequestReference changeRequest)
    {
        if (!string.Equals(
                expectedProvider,
                changeRequest.ProviderName,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                expectedRevision,
                changeRequest.RevisionSha,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(changeRequest.ProviderId)
            || string.IsNullOrWhiteSpace(changeRequest.Number)
            || !changeRequest.Url.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                "Source-control provider returned an inconsistent change request.");
        }
    }

    private static string CreateTitle(AgentWorkItemResultV1 item)
    {
        string summary = item.Candidate!.Value
            .GetProperty("summary")
            .GetString()!;
        string title = $"{item.JiraKey}: {summary}";
        return title.Length <= 240 ? title : title[..240];
    }

    private static string CreateBody(
        RepositorySessionRecord session,
        AgentWorkItemResultV1 item)
    {
        JsonElement candidate = item.Candidate!.Value;
        JsonElement judgment = item.Judgment!.Value;
        var body = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"- Jira issue: {item.JiraKey}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Agent session ID: {session.Id}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Base commit: {item.BaseCommitSha}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Candidate commit: {item.CandidateCommitSha}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Summary: {candidate.GetProperty("summary").GetString()}")
            .AppendLine("- Acceptance-criteria evidence:");
        foreach (JsonElement evidence in
                 candidate.GetProperty("acceptance_criteria_evidence")
                     .EnumerateArray())
        {
            body.AppendLine(
                CultureInfo.InvariantCulture,
                $"  - {evidence.GetProperty("criterion").GetString()}: {evidence.GetProperty("evidence").GetString()}");
        }

        body.AppendLine("- Deterministic verification: passed")
            .AppendLine(
                CultureInfo.InvariantCulture,
                $"- Judge disposition: {judgment.GetProperty("disposition").GetString()}")
            .AppendLine(
                CultureInfo.InvariantCulture,
                $"- Judge summary: {judgment.GetProperty("summary").GetString()}")
            .AppendLine("- Known risks:");
        foreach (JsonElement risk in
                 candidate.GetProperty("known_risks").EnumerateArray())
        {
            body.AppendLine(
                CultureInfo.InvariantCulture,
                $"  - {risk.GetString()}");
        }

        return body.ToString();
    }

    private static string? GetRawText(JsonElement? value) =>
        value is null || value.Value.ValueKind == JsonValueKind.Null
            ? null
            : value.Value.GetRawText();

    private static string GetRequiredPropertyRawText(
        string json,
        string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetRawText();
    }

    private static void EnsureUnique<T>(
        IEnumerable<T> values,
        string name)
        where T : notnull
    {
        var unique = new HashSet<T>();
        foreach (T value in values)
        {
            if (!unique.Add(value))
            {
                throw new AgentResultValidationException(
                    $"Duplicate {name} '{value}'.");
            }
        }
    }

    private static void RequireEqual<T>(
        T expected,
        T actual,
        string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AgentResultValidationException(
                $"{name} mismatch: expected '{expected}', actual '{actual}'.");
        }
    }
}
