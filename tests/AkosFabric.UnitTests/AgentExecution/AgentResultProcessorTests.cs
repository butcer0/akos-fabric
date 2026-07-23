using System.Globalization;
using System.Text.Json;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.AgentExecution.Services;
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

namespace AkosFabric.UnitTests.AgentExecution;

public sealed class AgentResultProcessorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c");
    private static readonly Guid WorkItemId =
        Guid.Parse("5e8a8ae4-65b2-4db8-aa62-949121cbd5f3");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse(
            "2026-07-23T14:07:00Z",
            CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset JiraUpdatedAt =
        DateTimeOffset.Parse(
            "2026-07-23T11:45:02Z",
            CultureInfo.InvariantCulture);
    private static readonly string ProfileSha = new('a', 40);
    private static readonly string BaseSha = new('b', 40);
    private static readonly string CandidateSha = new('c', 40);

    [Fact]
    public async Task RefusesToReadArtifactsBeforeContainerExit()
    {
        TestContext context = CreateContext();
        RepositorySessionContainer running = CreateContainer() with
        {
            State = RepositorySessionContainerState.Running,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Processor.ProcessAsync(
                running,
                CancellationToken.None));

        Assert.Equal(0, context.Reader.ReadCount);
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task PersistsResultBeforeCreatingDraftAndSynchronizingJira()
    {
        TestContext context = CreateContext();

        await context.Processor.ProcessAsync(
            CreateContainer(),
            CancellationToken.None);

        Assert.Equal(
            [
                "record-result",
                "branch-head",
                "find-change-request",
                "create-change-request",
                "record-change-request",
                "jira-review",
                "jira-comment",
                "complete",
            ],
            context.Calls);
        Assert.Equal(
            RepositorySessionStatus.Completed,
            context.Repository.Session.Status);
        Assert.Equal(
            WorkItemRunStatus.ChangeRequestCreated,
            context.Repository.Items[0].Status);
        Assert.NotNull(context.Provider.CreatedRequest);
        Assert.True(context.Provider.CreatedRequest.IsDraft);
        Assert.Contains(
            SessionId.ToString(),
            context.Provider.CreatedRequest.Body);
        Assert.Contains(CandidateSha, context.Provider.CreatedRequest.Body);
        Assert.Equal(
            "Agent change request: https://github.test/change/42",
            Assert.Single(context.Jira.Comments));
        Assert.Equal(
            [
                "work-item:github:branch_pushed",
                "model:gemini:gemini-3.6-flash:planner:1:10:5:0.25",
                "model:gemini:gemini-3.6-flash:coder:1:20:10:0.5",
                "model:gemini:gemini-3.6-flash:judge:1:8:4:0.5",
                "judge:github:accept",
                "change-request:github",
                "session-duration:github:completed:3600",
            ],
            context.Metrics.Events);
    }

    [Fact]
    public async Task InvalidCrossCheckFailsSessionBeforeProviderOrJiraCalls()
    {
        TestContext context = CreateContext();
        AgentSessionArtifactsV1 artifacts = context.Reader.Artifacts;
        AgentWorkItemResultV1 invalidItem = artifacts.Result.Items[0] with
        {
            WorkItemRunId = Guid.NewGuid(),
        };
        context.Reader.Artifacts = artifacts with
        {
            Result = artifacts.Result with
            {
                Items = [invalidItem],
            },
            ItemResultJson = new Dictionary<Guid, string>
            {
                [invalidItem.WorkItemRunId] = """{"status":"branch_pushed"}""",
            },
        };

        await context.Processor.ProcessAsync(
            CreateContainer(),
            CancellationToken.None);

        Assert.Equal(["fail-result"], context.Calls);
        Assert.Equal(
            RepositorySessionStatus.Failed,
            context.Repository.Session.Status);
        Assert.Equal(0, context.Provider.TotalCalls);
        Assert.Equal(0, context.Jira.TotalCalls);
    }

    [Fact]
    public async Task MissingResultFailureMarksSessionFailedBeforeExternalCalls()
    {
        TestContext context = CreateContext();
        context.Reader.ReadException = new AgentResultValidationException(
            "Cannot read completed result.json.");

        await context.Processor.ProcessAsync(
            CreateContainer(),
            CancellationToken.None);

        Assert.Equal(["fail-result"], context.Calls);
        Assert.Equal(
            RepositorySessionStatus.Failed,
            context.Repository.Session.Status);
        Assert.Equal(
            "invalid_agent_result",
            context.Repository.Session.FailureCode);
        Assert.Equal(0, context.Provider.TotalCalls);
        Assert.Equal(0, context.Jira.TotalCalls);
    }

    [Fact]
    public async Task JiraFailureRecordsWarningWithoutInvalidatingChangeRequest()
    {
        TestContext context = CreateContext();
        context.Jira.FailNextComment = true;

        await context.Processor.ProcessAsync(
            CreateContainer(),
            CancellationToken.None);

        Assert.Equal(
            RepositorySessionStatus.Completed,
            context.Repository.Session.Status);
        Assert.Equal(
            WorkItemRunStatus.ChangeRequestCreated,
            context.Repository.Items[0].Status);
        Assert.Equal(1, context.Provider.CreateCount);
        Assert.Contains(
            "jira-warning:jira_comment_failed",
            context.Calls);
        int metricEventCount = context.Metrics.Events.Count;

        await context.Processor.ProcessAsync(
            CreateContainer(),
            CancellationToken.None);

        Assert.Equal(1, context.Provider.CreateCount);
        Assert.Equal(2, context.Provider.BranchHeadCount);
        Assert.Single(context.Jira.Comments);
        Assert.Equal(
            metricEventCount,
            context.Metrics.Events.Count);
        Assert.Equal(
            RepositorySessionStatus.Completed,
            context.Repository.Session.Status);
    }

    [Fact]
    public async Task RemoteShaMismatchFailsWithoutCreatingChangeRequest()
    {
        TestContext context = CreateContext();
        context.Provider.RemoteHeadSha = new string('f', 40);

        await context.Processor.ProcessAsync(
            CreateContainer(),
            CancellationToken.None);

        Assert.Equal(
            ["record-result", "branch-head", "fail-result"],
            context.Calls);
        Assert.Equal(0, context.Provider.CreateCount);
        Assert.Equal(0, context.Jira.TotalCalls);
        Assert.Equal(
            RepositorySessionStatus.Failed,
            context.Repository.Session.Status);
        Assert.Equal(
            "remote_branch_sha_mismatch",
            context.Repository.Session.FailureCode);
    }

    private static TestContext CreateContext()
    {
        var calls = new List<string>();
        RepositoryProfile profile = CreateProfile();
        AgentSessionArtifactsV1 artifacts = CreateArtifacts(profile);
        var reader = new FakeArtifactReader(artifacts);
        var repository = new FakeRepository(
            CreateSession(),
            [CreateWorkItem()],
            calls);
        var provider = new FakeSourceControlProvider(calls);
        var jira = new FakeJiraClient(calls);
        var metrics = new FakeMetrics();
        var processor = new AgentResultProcessor(
            reader,
            repository,
            new FakeProfileProvider(profile),
            new FakeSourceControlResolver(provider),
            jira,
            new FixedTimeProvider(Now),
            metrics);
        return new TestContext(
            processor,
            reader,
            repository,
            provider,
            jira,
            calls,
            metrics);
    }

    private static AgentSessionArtifactsV1 CreateArtifacts(
        RepositoryProfile profile)
    {
        var manifestItem = new AgentWorkItemManifestV1(
            WorkItemId,
            1,
            "KAN-1",
            JiraUpdatedAt,
            Json("""{"id":"jira-1"}"""));
        var manifest = new AgentSessionManifestV1(
            1,
            SessionId,
            profile.Id,
            profile.ProfileRevisionSha,
            profile.Image.ExpectedDigest,
            new AgentSourceControlV1(
                profile.SourceControl.Provider,
                profile.SourceControl.BaseUrl),
            new AgentRepositoryV1(
                profile.Repository.ProviderRepositoryId,
                profile.Repository.CloneUrl,
                profile.Repository.DefaultBranch,
                profile.Repository.CloneStrategy,
                profile.Repository.GitLfs,
                profile.Repository.Submodules),
            [],
            new AgentLlmV1(
                profile.Llm.Provider,
                profile.Llm.ModelId,
                profile.Llm.OpenHandsModel),
            [manifestItem],
            new AgentSessionBehaviorV1(true),
            new AgentSessionLimitsV1(
                14400,
                5,
                25,
                30,
                3000,
                2,
                60));
        var resultItem = new AgentWorkItemResultV1(
            WorkItemId,
            "KAN-1",
            "branch_pushed",
            BaseSha,
            "agent/kan-1/5e8a8ae4",
            CandidateSha,
            ["src/example.cs"],
            Json("{}"),
            Json(
                """
                {
                  "summary": "Implement the requested change",
                  "acceptance_criteria_evidence": [
                    {
                      "criterion": "Behavior is verified",
                      "evidence": "Focused tests pass",
                      "paths": ["tests/example.cs"]
                    }
                  ],
                  "known_risks": []
                }
                """),
            Json("""{"passed":true,"commands":[]}"""),
            Json(
                $$"""
                {
                  "candidate_sha": "{{CandidateSha}}",
                  "disposition": "accept",
                  "summary": "Candidate is acceptable"
                }
                """),
            new AgentModelUsageV1(
                profile.Llm.Provider,
                profile.Llm.ModelId,
                Json(
                    """{"inputTokens":10,"outputTokens":5,"modelCalls":1,"estimatedCostUsd":0.25}"""),
                Json(
                    """{"inputTokens":20,"outputTokens":10,"modelCalls":1,"estimatedCostUsd":0.5}"""),
                Json(
                    """{"inputTokens":8,"outputTokens":4,"modelCalls":1,"estimatedCostUsd":0.5}"""),
                1.25m),
            null,
            null);
        var result = new AgentSessionResultV1(
            1,
            SessionId,
            "completed",
            Now.AddHours(-1),
            Now,
            null,
            null,
            new AgentResultRepositoryV1(
                profile.SourceControl.Provider,
                profile.Repository.ProviderRepositoryId,
                profile.Repository.CloneUrl),
            new AgentResultLlmV1(
                profile.Llm.Provider,
                profile.Llm.ModelId),
            [resultItem]);
        return new AgentSessionArtifactsV1(
            manifest,
            result,
            """{"status":"completed"}""",
            new Dictionary<Guid, string>
            {
                [WorkItemId] =
                    """
                    {
                      "status": "branch_pushed",
                      "modelUsage": {
                        "provider": "gemini",
                        "modelId": "gemini-3.6-flash"
                      }
                    }
                    """,
            });
    }

    private static RepositorySessionContainer CreateContainer() =>
        new(
            SessionId,
            "akos-fabric",
            $"agent-{SessionId:D}",
            new string('d', 64),
            RepositorySessionContainerState.Exited,
            0,
            Now.AddHours(-1),
            Now);

    private static RepositorySessionRecord CreateSession() =>
        new(
            SessionId,
            "akos-fabric",
            ProfileSha,
            "github",
            "akos-fabric-agent:development",
            $"sha256:{new string('d', 64)}",
            RepositorySessionStatus.Running,
            Guid.NewGuid(),
            $"agent-{SessionId:D}",
            new string('d', 64),
            "{}",
            "test-subject",
            "test-client",
            null,
            null,
            Now.AddHours(-2),
            Now.AddHours(-2),
            Now.AddHours(-1),
            null,
            null,
            null);

    private static WorkItemRunRecord CreateWorkItem() =>
        new(
            WorkItemId,
            SessionId,
            1,
            "jira-1",
            "KAN-1",
            JiraUpdatedAt,
            """{"id":"jira-1"}""",
            WorkItemRunStatus.Queued,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static RepositoryProfile CreateProfile() =>
        new(
            1,
            "akos-fabric",
            ProfileSha,
            new JiraRepositoryProfile(
                "default",
                "KAN",
                ["Story", "Bug"],
                "project = KAN",
                new JiraFieldProfile(
                    "id",
                    "key",
                    "summary",
                    "description",
                    "issuetype",
                    "status",
                    "priority",
                    "labels",
                    "updated"),
                new JiraWorkflowProfile(
                    "default-zero-configuration",
                    "To Do",
                    "In Progress",
                    "In Progress",
                    "Done",
                    "To Do",
                    true)),
            new SourceControlRepositoryProfile(
                "github",
                new Uri("https://github.test"),
                "source-control"),
            new RepositoryDefinition(
                "example/akos-fabric",
                new Uri("https://github.test/example/akos-fabric.git"),
                "main",
                "full",
                false,
                "none"),
            [],
            new LlmRepositoryProfile(
                "gemini",
                "gemini-3.6-flash",
                "gemini/gemini-3.6-flash",
                "gemini-development"),
            new ImageRepositoryProfile(
                "akos-fabric-agent:development",
                $"sha256:{new string('d', 64)}"),
            ["csharp"],
            new SerenaRepositoryProfile(
                "ide",
                "/opt/repository-profile/serena-project.yml"),
            new SessionRepositoryProfile(5, 240, true),
            new ItemRepositoryProfile(2, 60, 25, 30, 3000),
            [],
            new VerificationRepositoryProfile(
                [new ProcessCommand("test", ["dotnet", "test"], 120)]),
            new CiRepositoryProfile(
                "github-actions",
                new ReviewCommand(["python", "-m", "agent_runtime.ci_review"])));

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();

    private sealed record TestContext(
        AgentResultProcessor Processor,
        FakeArtifactReader Reader,
        FakeRepository Repository,
        FakeSourceControlProvider Provider,
        FakeJiraClient Jira,
        List<string> Calls,
        FakeMetrics Metrics);

    private sealed class FakeMetrics : IAgentControlMetrics
    {
        public List<string> Events { get; } = [];

        public void RecordRepositorySessionCreated(
            string sourceControlProvider) =>
            Events.Add($"session:{sourceControlProvider}");

        public void RecordRepositorySessionDuration(
            string sourceControlProvider,
            string outcome,
            TimeSpan duration) =>
            Events.Add(
                $"session-duration:{sourceControlProvider}:{outcome}:{duration.TotalSeconds}");

        public void RecordWorkItem(
            string sourceControlProvider,
            string outcome) =>
            Events.Add($"work-item:{sourceControlProvider}:{outcome}");

        public void RecordModelUsage(
            string modelProvider,
            string model,
            string role,
            long requestCount,
            long inputTokens,
            long outputTokens,
            decimal estimatedCostUsd) =>
            Events.Add(
                $"model:{modelProvider}:{model}:{role}:{requestCount}:{inputTokens}:{outputTokens}:{estimatedCostUsd}");

        public void RecordVerificationFailure(
            string sourceControlProvider) =>
            Events.Add($"verification-failure:{sourceControlProvider}");

        public void RecordJudgeDisposition(
            string sourceControlProvider,
            string disposition) =>
            Events.Add($"judge:{sourceControlProvider}:{disposition}");

        public void RecordChangeRequestCreated(
            string sourceControlProvider) =>
            Events.Add($"change-request:{sourceControlProvider}");
    }

    private sealed class FakeArtifactReader(AgentSessionArtifactsV1 artifacts)
        : IAgentSessionArtifactReader
    {
        public AgentSessionArtifactsV1 Artifacts { get; set; } = artifacts;

        public Exception? ReadException { get; set; }

        public int ReadCount { get; private set; }

        public Task<AgentSessionArtifactsV1> ReadAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            if (ReadException is not null)
            {
                throw ReadException;
            }

            return Task.FromResult(Artifacts);
        }
    }

    private sealed class FakeRepository(
        RepositorySessionRecord session,
        List<WorkItemRunRecord> items,
        List<string> calls)
        : IRepositorySessionRepository
    {
        public RepositorySessionRecord Session { get; private set; } = session;

        public List<WorkItemRunRecord> Items { get; } = items;

        public Task<RepositorySessionRecord?> FindAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            Task.FromResult<RepositorySessionRecord?>(
                id == Session.Id ? Session : null);

        public Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkItemRunRecord>>(Items);

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositorySessionRecord>>(
                [Session]);

        public Task<IReadOnlyList<RepositorySessionRecord>> ListRecoverableAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositorySessionRecord>>(
                Session.Status is RepositorySessionStatus.Starting
                    or RepositorySessionStatus.Running
                    or RepositorySessionStatus.ProcessingResults
                    ? [Session]
                    : []);

        public Task RecordValidatedResultAsync(
            AgentResultRecording result,
            CancellationToken cancellationToken)
        {
            calls.Add("record-result");
            Session = Session with
            {
                Status = RepositorySessionStatus.ProcessingResults,
            };
            foreach (AgentWorkItemOutcomeRecording outcome in result.WorkItems)
            {
                int index = Items.FindIndex(item => item.Id == outcome.WorkItemRunId);
                WorkItemRunRecord current = Items[index];
                if (current.Status != WorkItemRunStatus.ChangeRequestCreated)
                {
                    Items[index] = current with
                    {
                        Status = outcome.Status,
                        BaseCommitSha = outcome.BaseCommitSha,
                        BranchName = outcome.BranchName,
                        CandidateCommitSha = outcome.CandidateCommitSha,
                    };
                }
            }

            return Task.CompletedTask;
        }

        public Task RecordChangeRequestAsync(
            ChangeRequestRecording recording,
            CancellationToken cancellationToken)
        {
            calls.Add("record-change-request");
            int index = Items.FindIndex(item => item.Id == recording.WorkItemRunId);
            Items[index] = Items[index] with
            {
                Status = WorkItemRunStatus.ChangeRequestCreated,
                ChangeRequestId = recording.ChangeRequest.ProviderId,
                ChangeRequestNumber = recording.ChangeRequest.Number,
                ChangeRequestUrl = recording.ChangeRequest.Url.AbsoluteUri,
            };
            return Task.CompletedTask;
        }

        public Task RecordJiraSynchronizationWarningAsync(
            JiraSynchronizationWarningRecording recording,
            CancellationToken cancellationToken)
        {
            calls.Add($"jira-warning:{recording.FailureCode}");
            return Task.CompletedTask;
        }

        public Task FailResultProcessingAsync(
            AgentResultProcessingFailure failure,
            CancellationToken cancellationToken)
        {
            calls.Add("fail-result");
            Session = Session with
            {
                Status = RepositorySessionStatus.Failed,
                FailureCode = failure.FailureCode,
            };
            return Task.CompletedTask;
        }

        public Task CompleteResultProcessingAsync(
            AgentResultProcessingCompletion completion,
            CancellationToken cancellationToken)
        {
            calls.Add("complete");
            Session = Session with
            {
                Status = completion.FinalStatus,
            };
            return Task.CompletedTask;
        }

        public Task CreateAsync(
            RepositorySessionCreation session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            RepositorySessionCancellation cancellation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionSessionStatusAsync(
            RepositorySessionStatusTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionWorkItemStatusAsync(
            WorkItemRunStatusTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProfileProvider(RepositoryProfile profile)
        : IRepositoryProfileProvider
    {
        public Task<RepositoryProfile?> FindAsync(
            string profileName,
            CancellationToken cancellationToken) =>
            Task.FromResult<RepositoryProfile?>(
                profileName == profile.Id ? profile : null);
    }

    private sealed class FakeSourceControlResolver(
        FakeSourceControlProvider provider)
        : ISourceControlProviderResolver
    {
        public ISourceControlProvider Resolve(string providerName)
        {
            Assert.Equal(provider.ProviderName, providerName);
            return provider;
        }
    }

    private sealed class FakeSourceControlProvider(List<string> calls)
        : ISourceControlProvider
    {
        public string ProviderName => "github";

        public int CreateCount { get; private set; }

        public int BranchHeadCount { get; private set; }

        public int TotalCalls { get; private set; }

        public CreateChangeRequest? CreatedRequest { get; private set; }

        public string RemoteHeadSha { get; set; } = CandidateSha;

        public Task<string> GetBranchHeadShaAsync(
            SourceRepositoryReference repository,
            string branchName,
            CancellationToken cancellationToken)
        {
            calls.Add("branch-head");
            BranchHeadCount++;
            TotalCalls++;
            return Task.FromResult(RemoteHeadSha);
        }

        public Task<ChangeRequestReference?> FindOpenChangeRequestAsync(
            SourceRepositoryReference repository,
            string sourceBranch,
            CancellationToken cancellationToken)
        {
            calls.Add("find-change-request");
            TotalCalls++;
            return Task.FromResult<ChangeRequestReference?>(null);
        }

        public Task<ChangeRequestReference> CreateChangeRequestAsync(
            CreateChangeRequest request,
            CancellationToken cancellationToken)
        {
            calls.Add("create-change-request");
            CreateCount++;
            TotalCalls++;
            CreatedRequest = request;
            return Task.FromResult(
                new ChangeRequestReference(
                    ProviderName,
                    "provider-42",
                    "42",
                    new Uri("https://github.test/change/42"),
                    CandidateSha));
        }

        public Task<ChangeRequestReviewResult>
            UpsertInformationalReviewAsync(
                ChangeRequestReview review,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeJiraClient(List<string> calls)
        : IJiraClient
    {
        public bool FailNextComment { get; set; }

        public int TotalCalls { get; private set; }

        public List<string> Comments { get; } = [];

        public Task<JiraTransitionResult> TransitionIssueAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            JiraWorkflowTarget workflowTarget,
            CancellationToken cancellationToken)
        {
            calls.Add("jira-review");
            TotalCalls++;
            Assert.Equal(JiraWorkflowTarget.Review, workflowTarget);
            return Task.FromResult(
                new JiraTransitionResult(
                    JiraTransitionOutcome.Applied,
                    profile.Workflow.ReviewStatus,
                    "31"));
        }

        public Task AddCommentAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            string comment,
            CancellationToken cancellationToken)
        {
            calls.Add("jira-comment");
            TotalCalls++;
            if (FailNextComment)
            {
                FailNextComment = false;
                throw new InvalidOperationException("simulated Jira failure");
            }

            Comments.Add(comment);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JiraIssueSnapshot>> SearchIssuesAsync(
            JiraRepositoryProfile profile,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JiraIssueSnapshot?> FindIssueAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
