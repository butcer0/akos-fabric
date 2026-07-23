using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.Common.Exceptions;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.RepositorySessions.Services;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Application.Telemetry;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Domain.WorkItems;

namespace AkosFabric.UnitTests.RepositorySessions;

public sealed class RepositorySessionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 16, 0, 0, TimeSpan.Zero);
    private const string TraceParent =
        "00-11111111111111111111111111111111-2222222222222222-01";

    [Fact]
    public async Task CreatePersistsAuditedAggregateThenPublishesSameIdentity()
    {
        var fixture = new Fixture();

        RepositorySessionDetails result = await fixture.Service.CreateAsync(
            new CreateRepositorySessionInput(
                "akos-fabric",
                ["kan-2", "KAN-1"]),
            Caller(),
            CancellationToken.None);

        RepositorySessionCreation created = Assert.Single(fixture.Repository.Created);
        Assert.Equal("caller-subject", created.RequestedBySubject);
        Assert.Equal("caller-client", created.RequestedByClientId);
        Assert.Equal("caller-token", created.RequestedByTokenId);
        Assert.Equal("trace-id", created.TraceId);
        Assert.Equal(["KAN-2", "KAN-1"], created.WorkItems.Select(x => x.JiraKey));
        Assert.Equal([1, 2], created.WorkItems.Select(x => x.SequenceNumber));
        Assert.Equal(RepositorySessionStatus.Published, result.Session.Status);

        RepositorySessionRequestedV1 message = Assert.Single(fixture.Queue.Messages);
        Assert.Equal(created.Id, message.RepositorySessionId);
        Assert.Equal(created.MessageId, message.MessageId);
        Assert.Equal(TraceParent, message.TraceParent);
        Assert.Equal(["KAN-2", "KAN-1"], fixture.Jira.AssignedKeys);
        Assert.Equal(
            [
                ("KAN-2", created.Id.ToString("D")),
                ("KAN-1", created.Id.ToString("D")),
            ],
            fixture.Jira.Comments);
        Assert.Equal("github", fixture.Resolver.ResolvedProvider);
        Assert.Equal(["session:github"], fixture.Metrics.Events);
    }

    [Fact]
    public async Task PublishFailureLeavesCommittedSessionCreated()
    {
        var fixture = new Fixture();
        fixture.Queue.Exception = new IOException("broker unavailable");

        RepositorySessionPublicationException exception =
            await Assert.ThrowsAsync<RepositorySessionPublicationException>(
                () => fixture.Service.CreateAsync(
                    new CreateRepositorySessionInput("akos-fabric", ["KAN-1"]),
                    Caller(),
                    CancellationToken.None));

        RepositorySessionCreation created = Assert.Single(fixture.Repository.Created);
        Assert.Equal(created.Id, exception.RepositorySessionId);
        Assert.Equal(
            RepositorySessionStatus.Created,
            fixture.Repository.Sessions[created.Id].Status);
        Assert.Empty(fixture.Jira.AssignedKeys);
    }

    [Fact]
    public async Task CreateRejectsMoreItemsThanProfileAllowsBeforeJiraReads()
    {
        var fixture = new Fixture(maxItems: 1);

        await Assert.ThrowsAsync<RepositorySessionValidationException>(
            () => fixture.Service.CreateAsync(
                new CreateRepositorySessionInput(
                    "akos-fabric",
                    ["KAN-1", "KAN-2"]),
                Caller(),
                CancellationToken.None));

        Assert.Empty(fixture.Jira.RequestedKeys);
        Assert.Empty(fixture.Repository.Created);
    }

    [Fact]
    public async Task RetryTerminalSessionCreatesFreshAggregateAndMessageIdentities()
    {
        var fixture = new Fixture();
        RepositorySessionDetails original = await fixture.Service.CreateAsync(
            new CreateRepositorySessionInput("akos-fabric", ["KAN-1"]),
            Caller(),
            CancellationToken.None);
        fixture.Repository.SetStatus(
            original.Session.Id,
            RepositorySessionStatus.Cancelled);

        RepositorySessionDetails retried = await fixture.Service.RetryAsync(
            original.Session.Id,
            Caller(),
            CancellationToken.None);

        Assert.NotEqual(original.Session.Id, retried.Session.Id);
        Assert.NotEqual(original.Session.MessageId, retried.Session.MessageId);
        Assert.NotEqual(
            original.WorkItems.Single().Id,
            retried.WorkItems.Single().Id);
        Assert.Equal(RepositorySessionStatus.Published, retried.Session.Status);
        Assert.Equal(2, fixture.Queue.Messages.Count);
    }

    [Fact]
    public async Task CancelRunningStopsExecutorBeforeTransactionalCancellation()
    {
        var fixture = new Fixture();
        RepositorySessionDetails created = await fixture.Service.CreateAsync(
            new CreateRepositorySessionInput("akos-fabric", ["KAN-1"]),
            Caller(),
            CancellationToken.None);
        fixture.Repository.SetStatus(
            created.Session.Id,
            RepositorySessionStatus.Running);
        fixture.Executor.OnStop = () =>
            Assert.False(fixture.Repository.CancelCalled);

        RepositorySessionDetails cancelled = await fixture.Service.CancelAsync(
            created.Session.Id,
            CancellationToken.None);

        Assert.Equal(created.Session.Id, fixture.Executor.StoppedSessionId);
        Assert.True(fixture.Repository.CancelCalled);
        Assert.Equal(RepositorySessionStatus.Cancelled, cancelled.Session.Status);
        Assert.All(
            cancelled.WorkItems,
            item => Assert.Equal(WorkItemRunStatus.Failed, item.Status));
    }

    [Fact]
    public async Task PublishOnlyAcceptsCreatedAndReusesMessageIdentity()
    {
        var fixture = new Fixture();
        RepositorySessionDetails created = await fixture.Service.CreateAsync(
            new CreateRepositorySessionInput("akos-fabric", ["KAN-1"]),
            Caller(),
            CancellationToken.None);

        await Assert.ThrowsAsync<RepositorySessionConflictException>(
            () => fixture.Service.PublishAsync(
                created.Session.Id,
                CancellationToken.None));

        Assert.Single(fixture.Queue.Messages);
    }

    [Fact]
    public async Task ListReturnsRepositorySessionsWithinOperatorLimit()
    {
        var fixture = new Fixture();
        RepositorySessionDetails first = await fixture.Service.CreateAsync(
            new CreateRepositorySessionInput("akos-fabric", ["KAN-1"]),
            Caller(),
            CancellationToken.None);
        RepositorySessionDetails second = await fixture.Service.CreateAsync(
            new CreateRepositorySessionInput("akos-fabric", ["KAN-2"]),
            Caller(),
            CancellationToken.None);

        IReadOnlyList<RepositorySessionRecord> sessions =
            await fixture.Service.ListAsync(1, CancellationToken.None);

        RepositorySessionRecord result = Assert.Single(sessions);
        Assert.Contains(result.Id, new[] { first.Session.Id, second.Session.Id });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task ListRejectsUnboundedOperatorQueries(int limit)
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<RepositorySessionValidationException>(
            () => fixture.Service.ListAsync(limit, CancellationToken.None));
    }

    [Fact]
    public async Task JiraAssignmentFailuresBecomeDurableWarnings()
    {
        var fixture = new Fixture();
        fixture.Jira.TransitionOutcome =
            JiraTransitionOutcome.Unavailable;
        fixture.Jira.CommentException =
            new HttpRequestException("sensitive upstream detail");

        RepositorySessionDetails result = await fixture.Service.CreateAsync(
            new CreateRepositorySessionInput("akos-fabric", ["KAN-1"]),
            Caller(),
            CancellationToken.None);

        Assert.Equal(
            RepositorySessionStatus.Published,
            result.Session.Status);
        Assert.Equal(
            [
                "jira_transition_unavailable",
                "jira_comment_failed",
            ],
            fixture.Repository.JiraWarnings.Select(
                warning => warning.FailureCode));
        Assert.All(
            fixture.Repository.JiraWarnings,
            warning =>
                Assert.DoesNotContain(
                    "sensitive upstream detail",
                    warning.PayloadJson,
                    StringComparison.Ordinal));
    }

    private static RepositorySessionCaller Caller() =>
        new(
            "caller-subject",
            "caller-client",
            "caller-token",
            "trace-id",
            TraceParent);

    private sealed class Fixture
    {
        public Fixture(int maxItems = 5)
        {
            Profile = CreateProfile(maxItems);
            ProfileProvider = new FakeProfileProvider(Profile);
            Jira = new FakeJiraClient();
            Repository = new FakeRepository();
            Queue = new FakeQueue();
            Resolver = new FakeResolver();
            Executor = new FakeExecutor();
            Metrics = new FakeMetrics();
            Service = new RepositorySessionService(
                ProfileProvider,
                Jira,
                Repository,
                Queue,
                Resolver,
                Executor,
                new FixedTimeProvider(Now),
                Metrics);
        }

        public RepositoryProfile Profile { get; }
        public FakeProfileProvider ProfileProvider { get; }
        public FakeJiraClient Jira { get; }
        public FakeRepository Repository { get; }
        public FakeQueue Queue { get; }
        public FakeResolver Resolver { get; }
        public FakeExecutor Executor { get; }
        public FakeMetrics Metrics { get; }
        public RepositorySessionService Service { get; }
    }

    private sealed class FakeMetrics : IAgentControlMetrics
    {
        public List<string> Events { get; } = [];

        public void RecordRepositorySessionCreated(
            string sourceControlProvider) =>
            Events.Add($"session:{sourceControlProvider}");

        public void RecordRepositorySessionDuration(
            string sourceControlProvider,
            string outcome,
            TimeSpan duration)
        {
        }

        public void RecordWorkItem(
            string sourceControlProvider,
            string outcome)
        {
        }

        public void RecordModelUsage(
            string modelProvider,
            string model,
            string role,
            long requestCount,
            long inputTokens,
            long outputTokens,
            decimal estimatedCostUsd)
        {
        }

        public void RecordVerificationFailure(
            string sourceControlProvider)
        {
        }

        public void RecordJudgeDisposition(
            string sourceControlProvider,
            string disposition)
        {
        }

        public void RecordChangeRequestCreated(
            string sourceControlProvider)
        {
        }
    }

    private sealed class FakeProfileProvider(RepositoryProfile profile)
        : IRepositoryProfileProvider
    {
        public Task<RepositoryProfile?> FindAsync(
            string profileName,
            CancellationToken cancellationToken) =>
            Task.FromResult<RepositoryProfile?>(
                string.Equals(profileName, profile.Id, StringComparison.Ordinal)
                    ? profile
                    : null);
    }

    private sealed class FakeJiraClient : IJiraClient
    {
        public List<string> RequestedKeys { get; } = [];
        public List<string> AssignedKeys { get; } = [];
        public List<(string IssueKey, string Comment)> Comments { get; } = [];
        public JiraTransitionOutcome TransitionOutcome { get; set; } =
            JiraTransitionOutcome.Applied;
        public Exception? CommentException { get; set; }

        public Task<IReadOnlyList<JiraIssueSnapshot>> SearchIssuesAsync(
            JiraRepositoryProfile profile,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JiraIssueSnapshot>>([]);

        public Task<JiraIssueSnapshot?> FindIssueAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            CancellationToken cancellationToken)
        {
            RequestedKeys.Add(issueKey);
            string number = issueKey.Split('-')[1];
            return Task.FromResult<JiraIssueSnapshot?>(
                new JiraIssueSnapshot(
                    number,
                    issueKey,
                    $"Issue {number}",
                    "Complete issue description",
                    "Story",
                    "To Do",
                    "High",
                    [],
                    Now,
                    $$"""{"id":"{{number}}","key":"{{issueKey}}"}"""));
        }

        public Task<JiraTransitionResult> TransitionIssueAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            JiraWorkflowTarget workflowTarget,
            CancellationToken cancellationToken)
        {
            Assert.Equal(JiraWorkflowTarget.Assigned, workflowTarget);
            AssignedKeys.Add(issueKey);
            return Task.FromResult(
                new JiraTransitionResult(
                    TransitionOutcome,
                    profile.Workflow.AssignedStatus,
                    TransitionOutcome == JiraTransitionOutcome.Applied
                        ? "21"
                        : null));
        }

        public Task AddCommentAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            string comment,
            CancellationToken cancellationToken)
        {
            if (CommentException is not null)
            {
                throw CommentException;
            }

            Comments.Add((issueKey, comment));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepository : IRepositorySessionRepository
    {
        public List<RepositorySessionCreation> Created { get; } = [];
        public Dictionary<Guid, RepositorySessionRecord> Sessions { get; } = [];
        public Dictionary<Guid, List<WorkItemRunRecord>> Items { get; } = [];
        public bool CancelCalled { get; private set; }
        public List<JiraSynchronizationWarningRecording> JiraWarnings { get; } =
            [];

        public Task<RepositorySessionRecord?> FindAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            Task.FromResult<RepositorySessionRecord?>(
                Sessions.GetValueOrDefault(id));

        public Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkItemRunRecord>>(
                Items.GetValueOrDefault(repositorySessionId) ?? []);

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositorySessionRecord>>(
                Sessions.Values.Take(limit).ToArray());

        public Task<IReadOnlyList<RepositorySessionRecord>> ListRecoverableAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositorySessionRecord>>(
                Sessions.Values
                    .Where(
                        session => session.Status is
                            RepositorySessionStatus.Starting
                            or RepositorySessionStatus.Running
                            or RepositorySessionStatus.ProcessingResults)
                    .ToArray());

        public Task CreateAsync(
            RepositorySessionCreation session,
            CancellationToken cancellationToken)
        {
            Created.Add(session);
            Sessions.Add(session.Id, ToRecord(session));
            Items.Add(
                session.Id,
                session.WorkItems.Select(
                        item => new WorkItemRunRecord(
                            item.Id,
                            session.Id,
                            item.SequenceNumber,
                            item.JiraIssueId,
                            item.JiraKey,
                            item.JiraUpdatedAt,
                            item.JiraSnapshotJson,
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
                            null))
                    .ToList());
            return Task.CompletedTask;
        }

        public Task CancelAsync(
            RepositorySessionCancellation cancellation,
            CancellationToken cancellationToken)
        {
            CancelCalled = true;
            SetStatus(
                cancellation.RepositorySessionId,
                RepositorySessionStatus.Cancelled);
            Items[cancellation.RepositorySessionId] = Items[
                    cancellation.RepositorySessionId]
                .Select(item => item with
                {
                    Status = WorkItemRunStatus.Failed,
                    FailureCode = "session_cancelled",
                })
                .ToList();
            return Task.CompletedTask;
        }

        public Task TransitionSessionStatusAsync(
            RepositorySessionStatusTransition transition,
            CancellationToken cancellationToken)
        {
            Assert.Equal(
                transition.ExpectedStatus,
                Sessions[transition.RepositorySessionId].Status);
            SetStatus(transition.RepositorySessionId, transition.NewStatus);
            return Task.CompletedTask;
        }

        public Task TransitionWorkItemStatusAsync(
            WorkItemRunStatusTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordValidatedResultAsync(
            AgentResultRecording result,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordChangeRequestAsync(
            ChangeRequestRecording recording,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordJiraSynchronizationWarningAsync(
            JiraSynchronizationWarningRecording recording,
            CancellationToken cancellationToken)
        {
            JiraWarnings.Add(recording);
            return Task.CompletedTask;
        }

        public Task FailResultProcessingAsync(
            AgentResultProcessingFailure failure,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteResultProcessingAsync(
            AgentResultProcessingCompletion completion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void SetStatus(Guid id, RepositorySessionStatus status)
        {
            RepositorySessionRecord current = Sessions[id];
            Sessions[id] = current with
            {
                Status = status,
                PublishedAt =
                    status == RepositorySessionStatus.Published
                        ? Now
                        : current.PublishedAt,
                CompletedAt =
                    status is RepositorySessionStatus.Completed
                        or RepositorySessionStatus.Failed
                        or RepositorySessionStatus.Cancelled
                            ? Now
                            : current.CompletedAt,
            };
        }

        private static RepositorySessionRecord ToRecord(
            RepositorySessionCreation session) =>
            new(
                session.Id,
                session.RepositoryProfile,
                session.ProfileRevisionSha,
                session.SourceControlProvider,
                session.ImageReference,
                session.ImageDigest,
                RepositorySessionStatus.Created,
                session.MessageId,
                null,
                null,
                session.RequestPayloadJson,
                session.RequestedBySubject,
                session.RequestedByClientId,
                session.RequestedByTokenId,
                session.TraceId,
                session.CreatedAt,
                null,
                null,
                null,
                null,
                null);
    }

    private sealed class FakeQueue : IRepositorySessionQueue
    {
        public List<RepositorySessionRequestedV1> Messages { get; } = [];
        public Exception? Exception { get; set; }

        public Task PublishAsync(
            RepositorySessionRequestedV1 message,
            CancellationToken cancellationToken)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResolver : ISourceControlProviderResolver
    {
        public string? ResolvedProvider { get; private set; }

        public ISourceControlProvider Resolve(string providerName)
        {
            ResolvedProvider = providerName;
            return new FakeSourceControlProvider(providerName);
        }
    }

    private sealed class FakeSourceControlProvider(string providerName)
        : ISourceControlProvider
    {
        public string ProviderName => providerName;

        public Task<ChangeRequestReference> CreateChangeRequestAsync(
            CreateChangeRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> GetBranchHeadShaAsync(
            SourceRepositoryReference repository,
            string branchName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChangeRequestReference?> FindOpenChangeRequestAsync(
            SourceRepositoryReference repository,
            string sourceBranch,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChangeRequestReviewResult>
            UpsertInformationalReviewAsync(
                ChangeRequestReview review,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeExecutor : IRepositorySessionExecutor
    {
        public Guid? StoppedSessionId { get; private set; }
        public Action? OnStop { get; set; }

        public Task<RepositorySessionExecution> StartAsync(
            RepositorySessionExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionContainer?> InspectAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionWaitResult> WaitAsync(
            Guid repositorySessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StreamLogsAsync(
            Guid repositorySessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplaceCredentialAsync(
            Guid repositorySessionId,
            SourceControlCredential credential,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteCredentialAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StopAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            OnStop?.Invoke();
            StoppedSessionId = repositorySessionId;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionContainer>> ListManagedAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static RepositoryProfile CreateProfile(int maxItems) =>
        new(
            1,
            "akos-fabric",
            new string('a', 40),
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
                new Uri("https://api.github.com"),
                "akos-fabric"),
            new RepositoryDefinition(
                "butcer0/akos-fabric",
                new Uri("https://github.com/butcer0/akos-fabric.git"),
                "main",
                "full",
                false,
                "none"),
            [],
            new LlmRepositoryProfile(
                "gemini",
                "gemini-3.6-flash",
                "gemini/gemini-3.6-flash",
                "default"),
            new ImageRepositoryProfile(
                "akos-fabric-agent:1.4",
                $"sha256:{new string('b', 64)}"),
            ["csharp", "python"],
            new SerenaRepositoryProfile("ide-assistant", ".serena/project.yml"),
            new SessionRepositoryProfile(maxItems, 60, false),
            new ItemRepositoryProfile(2, 10, 5, 20, 1000),
            [],
            new VerificationRepositoryProfile([]),
            new CiRepositoryProfile("github-actions", new ReviewCommand(["review"])));
}
