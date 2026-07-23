using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.IntegrationTests.Execution;

public sealed class ContainerLifecycleMonitorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExitedContainerProcessesResultThenCleansSensitiveState()
    {
        TestContext context = CreateContext();
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Running));
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Exited));

        await context.Monitor.MonitorAsync(
            TestIds.Session,
            CancellationToken.None);

        Assert.Equal(
            [
                "inspect",
                "acquire-credential",
                $"replace-credential:{TestIds.Session}",
                "logs",
                "wait",
                "inspect",
                "process-result",
                $"delete-credential:{TestIds.Session}",
                $"remove:{TestIds.Session}",
                $"retain:{TestIds.Session}",
            ],
            context.Calls);
    }

    [Fact]
    public async Task DeadlineTimeoutStopsAndFailsBeforeCleanup()
    {
        TestContext context = CreateContext();
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Running));
        context.Executor.WaitResult =
            new RepositorySessionWaitResult(true, null);

        await context.Monitor.MonitorAsync(
            TestIds.Session,
            CancellationToken.None);

        Assert.Contains("stop", context.Calls);
        Assert.Contains("fail:session_deadline_exceeded", context.Calls);
        Assert.DoesNotContain("process-result", context.Calls);
        Assert.Equal(
            [
                $"delete-credential:{TestIds.Session}",
                $"remove:{TestIds.Session}",
                $"retain:{TestIds.Session}",
            ],
            context.Calls.TakeLast(3));
    }

    [Fact]
    public async Task AlreadyExpiredDeadlineDoesNotWait()
    {
        TestContext context = CreateContext(
            startedAt: Now.AddMinutes(-241));
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Running,
            startedAt: Now.AddMinutes(-241)));

        await context.Monitor.MonitorAsync(
            TestIds.Session,
            CancellationToken.None);

        Assert.DoesNotContain("wait", context.Calls);
        Assert.DoesNotContain("logs", context.Calls);
        Assert.Contains("stop", context.Calls);
        Assert.Contains("fail:session_deadline_exceeded", context.Calls);
    }

    [Fact]
    public async Task MissingContainerUsesRetainedResultRecovery()
    {
        TestContext context = CreateContext();
        context.Executor.Inspections.Enqueue(null);

        await context.Monitor.MonitorAsync(
            TestIds.Session,
            CancellationToken.None);

        Assert.Equal(
            [
                "inspect",
                "process-recovered",
                $"delete-credential:{TestIds.Session}",
                $"remove:{TestIds.Session}",
                $"retain:{TestIds.Session}",
            ],
            context.Calls);
    }

    [Fact]
    public async Task ResultSynchronizationFailureStillDeletesCredentialAndContainer()
    {
        TestContext context = CreateContext();
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Running));
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Exited));
        context.ResultProcessor.ProcessException =
            new InvalidOperationException("provider unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Monitor.MonitorAsync(
                TestIds.Session,
                CancellationToken.None));

        Assert.Equal(
            [
                $"delete-credential:{TestIds.Session}",
                $"remove:{TestIds.Session}",
                $"retain:{TestIds.Session}",
            ],
            context.Calls.TakeLast(3));
    }

    [Fact]
    public async Task ApplicationCancellationPreservesRunningContainerForRestart()
    {
        TestContext context = CreateContext();
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Running));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.Executor.WaitException =
            new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Monitor.MonitorAsync(
                TestIds.Session,
                cancellation.Token));

        Assert.DoesNotContain(
            context.Calls,
            call => call.StartsWith(
                "delete-credential:",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.Calls,
            call => call.StartsWith("remove:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.Calls,
            call => call.StartsWith("retain:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongRunningContainerRefreshesBeforeCredentialExpiry()
    {
        var timeProvider = new ManualTimeProvider(Now);
        TestContext context = CreateContext(
            timeProvider: timeProvider,
            monitorOptions: new RepositorySessionMonitorOptions
            {
                CredentialRefreshSafetyMargin = TimeSpan.FromMinutes(5),
            });
        context.Credentials.Credentials.Enqueue(
            new SourceControlCredential(
                "x-access-token",
                "first-secret",
                Now.AddMinutes(20)));
        context.Credentials.Credentials.Enqueue(
            new SourceControlCredential(
                "x-access-token",
                "second-secret",
                Now.AddMinutes(75)));
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Running));
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Exited));
        context.Executor.WaitResults.Enqueue(
            new RepositorySessionWaitResult(true, null));
        context.Executor.WaitResults.Enqueue(
            new RepositorySessionWaitResult(false, 0));
        context.Executor.OnWait = timeProvider.Advance;

        await context.Monitor.MonitorAsync(
            TestIds.Session,
            CancellationToken.None);

        Assert.Equal(
            [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(55)],
            context.Executor.WaitTimeouts);
        Assert.Equal(
            2,
            context.Calls.Count(
                call => call == "acquire-credential"));
        Assert.Equal(
            2,
            context.Calls.Count(
                call => call ==
                    $"replace-credential:{TestIds.Session}"));
        Assert.Contains("process-result", context.Calls);
    }

    [Fact]
    public async Task CredentialRefreshFailureStopsAndFailsSession()
    {
        TestContext context = CreateContext();
        context.Executor.Inspections.Enqueue(Container(
            RepositorySessionContainerState.Running));
        context.Credentials.Exception =
            new InvalidOperationException("credential endpoint unavailable");

        await context.Monitor.MonitorAsync(
            TestIds.Session,
            CancellationToken.None);

        Assert.Contains("stop", context.Calls);
        Assert.Contains(
            "fail:source_control_credential_refresh_failed",
            context.Calls);
        Assert.DoesNotContain("wait", context.Calls);
        Assert.DoesNotContain("process-result", context.Calls);
    }

    [Fact]
    public async Task StartupReconciliationScansOnceAndRecoversMissingSession()
    {
        var calls = new List<string>();
        RepositorySessionRecord attachedSession = Session(
            TestIds.Session,
            RepositorySessionStatus.Running);
        RepositorySessionRecord missingSession = Session(
            TestIds.OtherSession,
            RepositorySessionStatus.Running);
        var repository = new FakeRepository(
            [attachedSession, missingSession],
            calls);
        var executor = new FakeExecutor(calls)
        {
            ManagedContainers =
            [
                Container(RepositorySessionContainerState.Running),
            ],
        };
        var resultProcessor = new FakeResultProcessor(calls);
        var retention = new FakeRetentionScheduler(calls);
        var attacher = new FakeMonitorAttacher(calls);
        var credentials = new FakeCredentialAcquisition(calls);
        var reconciler = new RepositorySessionStartupReconciler(
            executor,
            repository,
            new FakeProfileProvider(Profile()),
            credentials,
            attacher,
            resultProcessor,
            retention,
            new RepositorySessionMonitorOptions(),
            new FixedTimeProvider(Now));

        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(1, executor.ListManagedCount);
        Assert.Equal([TestIds.Session], attacher.Attached);
        Assert.Equal(
            1,
            calls.Count(call => call == "acquire-credential"));
        Assert.True(
            calls.IndexOf($"replace-credential:{TestIds.Session}") <
            calls.IndexOf($"attach:{TestIds.Session}"));
        Assert.Equal([TestIds.OtherSession], resultProcessor.Recovered);
        Assert.Contains(
            $"delete-credential:{TestIds.OtherSession}",
            calls);
        Assert.Contains($"retain:{TestIds.OtherSession}", calls);
    }

    [Fact]
    public async Task StartupRefreshFailureFailsAndDoesNotReattach()
    {
        var calls = new List<string>();
        RepositorySessionRecord session = Session(
            TestIds.Session,
            RepositorySessionStatus.Running);
        var repository = new FakeRepository([session], calls);
        var executor = new FakeExecutor(calls)
        {
            ManagedContainers =
            [
                Container(RepositorySessionContainerState.Running),
            ],
        };
        var credentials = new FakeCredentialAcquisition(calls)
        {
            Exception =
                new InvalidOperationException(
                    "credential endpoint unavailable"),
        };
        var attacher = new FakeMonitorAttacher(calls);
        var reconciler = new RepositorySessionStartupReconciler(
            executor,
            repository,
            new FakeProfileProvider(Profile()),
            credentials,
            attacher,
            new FakeResultProcessor(calls),
            new FakeRetentionScheduler(calls),
            new RepositorySessionMonitorOptions(),
            new FixedTimeProvider(Now));

        await reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.Empty(attacher.Attached);
        Assert.Contains("stop", calls);
        Assert.Contains(
            "fail:source_control_credential_refresh_failed",
            calls);
        Assert.Contains(
            $"delete-credential:{TestIds.Session}",
            calls);
        Assert.Contains($"remove:{TestIds.Session}", calls);
        Assert.Contains($"retain:{TestIds.Session}", calls);
    }

    [Fact]
    public async Task StartupReconciliationEnforcesContainerBound()
    {
        var calls = new List<string>();
        var executor = new FakeExecutor(calls)
        {
            ManagedContainers =
            [
                Container(RepositorySessionContainerState.Running),
                Container(
                    RepositorySessionContainerState.Running,
                    TestIds.OtherSession),
            ],
        };
        var reconciler = new RepositorySessionStartupReconciler(
            executor,
            new FakeRepository([], calls),
            new FakeProfileProvider(Profile()),
            new FakeCredentialAcquisition(calls),
            new FakeMonitorAttacher(calls),
            new FakeResultProcessor(calls),
            new FakeRetentionScheduler(calls),
            new RepositorySessionMonitorOptions
            {
                MaximumStartupContainers = 1,
            },
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconciler.ReconcileOnceAsync(
                CancellationToken.None));

        Assert.Equal(1, executor.ListManagedCount);
    }

    private static TestContext CreateContext(
        DateTimeOffset? startedAt = null,
        TimeProvider? timeProvider = null,
        RepositorySessionMonitorOptions? monitorOptions = null)
    {
        var calls = new List<string>();
        RepositorySessionRecord session = Session(
            TestIds.Session,
            RepositorySessionStatus.Running,
            startedAt ?? Now.AddMinutes(-30));
        var repository = new FakeRepository([session], calls);
        var executor = new FakeExecutor(calls);
        var resultProcessor = new FakeResultProcessor(calls);
        var retention = new FakeRetentionScheduler(calls);
        var credentials = new FakeCredentialAcquisition(calls);
        RepositorySessionMonitorOptions resolvedMonitorOptions =
            monitorOptions ?? new RepositorySessionMonitorOptions();
        var monitor = new ContainerCompletionMonitor(
            executor,
            repository,
            new FakeProfileProvider(Profile()),
            resultProcessor,
            retention,
            credentials,
            resolvedMonitorOptions,
            timeProvider ?? new FixedTimeProvider(Now));
        return new TestContext(
            monitor,
            executor,
            repository,
            resultProcessor,
            credentials,
            calls);
    }

    private static RepositorySessionContainer Container(
        RepositorySessionContainerState state,
        Guid? id = null,
        DateTimeOffset? startedAt = null)
    {
        Guid sessionId = id ?? TestIds.Session;
        return new(
            sessionId,
            "akos-fabric",
            $"agent-{sessionId:D}",
            ContainerId(sessionId),
            state,
            state == RepositorySessionContainerState.Exited ? 0 : 0,
            startedAt ?? Now.AddMinutes(-30),
            state == RepositorySessionContainerState.Exited ? Now : null);
    }

    private static RepositorySessionRecord Session(
        Guid id,
        RepositorySessionStatus status,
        DateTimeOffset? startedAt = null) =>
        new(
            id,
            "akos-fabric",
            new string('a', 40),
            "github",
            "akos-fabric-agent:development",
            $"sha256:{new string('b', 64)}",
            status,
            Guid.NewGuid(),
            $"agent-{id:D}",
            ContainerId(id),
            "{}",
            "test-subject",
            "test-client",
            null,
            null,
            Now.AddHours(-1),
            Now.AddHours(-1),
            startedAt ?? Now.AddMinutes(-30),
            null,
            null,
            null);

    private static string ContainerId(Guid id) =>
        id.ToString("N") + id.ToString("N");

    private static RepositoryProfile Profile() =>
        new(
            1,
            "akos-fabric",
            new string('a', 40),
            new JiraRepositoryProfile(
                "default",
                "KAN",
                ["Story"],
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
                $"sha256:{new string('b', 64)}"),
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

    private sealed record TestContext(
        ContainerCompletionMonitor Monitor,
        FakeExecutor Executor,
        FakeRepository Repository,
        FakeResultProcessor ResultProcessor,
        FakeCredentialAcquisition Credentials,
        List<string> Calls);

    private sealed class FakeExecutor(List<string> calls)
        : IRepositorySessionExecutor
    {
        public Queue<RepositorySessionContainer?> Inspections { get; } = new();

        public RepositorySessionWaitResult WaitResult { get; set; } =
            new(false, 0);

        public Exception? WaitException { get; set; }

        public IReadOnlyList<RepositorySessionContainer> ManagedContainers
        {
            get;
            set;
        } = [];

        public int ListManagedCount { get; private set; }

        public List<TimeSpan> WaitTimeouts { get; } = [];

        public Action<TimeSpan>? OnWait { get; set; }

        public Queue<RepositorySessionWaitResult> WaitResults { get; } =
            new();

        public Task<RepositorySessionContainer?> InspectAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add("inspect");
            return Task.FromResult(
                Inspections.Count == 0 ? null : Inspections.Dequeue());
        }

        public Task<RepositorySessionWaitResult> WaitAsync(
            Guid repositorySessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            calls.Add("wait");
            WaitTimeouts.Add(timeout);
            OnWait?.Invoke(timeout);
            if (WaitException is not null)
            {
                throw WaitException;
            }

            return Task.FromResult(
                WaitResults.Count > 0
                    ? WaitResults.Dequeue()
                    : WaitResult);
        }

        public Task StreamLogsAsync(
            Guid repositorySessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            calls.Add("logs");
            return Task.CompletedTask;
        }

        public Task StopAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task DeleteCredentialAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add($"delete-credential:{repositorySessionId}");
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add($"remove:{repositorySessionId}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RepositorySessionContainer>> ListManagedAsync(
            CancellationToken cancellationToken)
        {
            calls.Add("list-managed");
            ListManagedCount++;
            return Task.FromResult(ManagedContainers);
        }

        public Task<RepositorySessionExecution> StartAsync(
            RepositorySessionExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplaceCredentialAsync(
            Guid repositorySessionId,
            SourceControlCredential credential,
            CancellationToken cancellationToken)
        {
            calls.Add($"replace-credential:{repositorySessionId}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepository(
        IEnumerable<RepositorySessionRecord> sessions,
        List<string> calls)
        : IRepositorySessionRepository
    {
        private readonly Dictionary<Guid, RepositorySessionRecord> sessions =
            sessions.ToDictionary(session => session.Id);

        public Task<RepositorySessionRecord?> FindAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            Task.FromResult<RepositorySessionRecord?>(
                sessions.GetValueOrDefault(id));

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositorySessionRecord>>(
                sessions.Values.Take(limit).ToArray());

        public Task<IReadOnlyList<RepositorySessionRecord>> ListRecoverableAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositorySessionRecord>>(
                sessions.Values
                    .Where(
                        session => session.Status is
                            RepositorySessionStatus.Starting
                            or RepositorySessionStatus.Running
                            or RepositorySessionStatus.ProcessingResults)
                    .ToArray());

        public Task TransitionSessionStatusAsync(
            RepositorySessionStatusTransition transition,
            CancellationToken cancellationToken)
        {
            RepositorySessionRecord session =
                sessions[transition.RepositorySessionId];
            sessions[transition.RepositorySessionId] = session with
            {
                Status = transition.NewStatus,
                ContainerName =
                    transition.ContainerName ?? session.ContainerName,
                ContainerId =
                    transition.ContainerId ?? session.ContainerId,
                StartedAt = transition.NewStatus ==
                            RepositorySessionStatus.Running
                    ? transition.OccurredAt
                    : session.StartedAt,
            };
            calls.Add($"transition:{transition.NewStatus}");
            return Task.CompletedTask;
        }

        public Task FailResultProcessingAsync(
            AgentResultProcessingFailure failure,
            CancellationToken cancellationToken)
        {
            RepositorySessionRecord session =
                sessions[failure.RepositorySessionId];
            sessions[failure.RepositorySessionId] = session with
            {
                Status = RepositorySessionStatus.Failed,
                FailureCode = failure.FailureCode,
                FailureMessage = failure.FailureMessage,
            };
            calls.Add($"fail:{failure.FailureCode}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkItemRunRecord>>([]);

        public Task CreateAsync(
            RepositorySessionCreation session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            RepositorySessionCancellation cancellation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task CompleteResultProcessingAsync(
            AgentResultProcessingCompletion completion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordJiraSynchronizationWarningAsync(
            JiraSynchronizationWarningRecording warning,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProfileProvider(RepositoryProfile profile)
        : IRepositoryProfileProvider
    {
        public Task<RepositoryProfile?> FindAsync(
            string profileName,
            CancellationToken cancellationToken) =>
            Task.FromResult<RepositoryProfile?>(profile);
    }

    private sealed class FakeResultProcessor(List<string> calls)
        : IAgentResultProcessor
    {
        public Exception? ProcessException { get; set; }

        public List<Guid> Recovered { get; } = [];

        public Task ProcessAsync(
            RepositorySessionContainer container,
            CancellationToken cancellationToken)
        {
            calls.Add("process-result");
            if (ProcessException is not null)
            {
                throw ProcessException;
            }

            return Task.CompletedTask;
        }

        public Task ProcessRecoveredAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add("process-recovered");
            Recovered.Add(repositorySessionId);
            if (ProcessException is not null)
            {
                throw ProcessException;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeRetentionScheduler(List<string> calls)
        : ISessionArtifactRetentionScheduler
    {
        public Task ScheduleAsync(
            Guid repositorySessionId,
            DateTimeOffset sessionFinishedAt,
            CancellationToken cancellationToken)
        {
            calls.Add($"retain:{repositorySessionId}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMonitorAttacher(List<string> calls)
        : IRepositorySessionMonitorAttacher
    {
        public List<Guid> Attached { get; } = [];

        public Task AttachAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add($"attach:{repositorySessionId}");
            Attached.Add(repositorySessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredentialAcquisition(List<string> calls)
        : ISourceControlCredentialAcquisitionService
    {
        public Queue<SourceControlCredential> Credentials { get; } = new();

        public Exception? Exception { get; set; }

        public Task<SourceControlCredential> AcquireForSessionAsync(
            RepositorySessionRecord session,
            RepositoryProfile profile,
            CancellationToken cancellationToken)
        {
            calls.Add("acquire-credential");
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(
                Credentials.Count > 0
                    ? Credentials.Dequeue()
                    : new SourceControlCredential(
                        "x-access-token",
                        "test-secret",
                        null));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private static class TestIds
    {
        public static readonly Guid Session =
            Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c");
        public static readonly Guid OtherSession =
            Guid.Parse("8f198bc5-bb8a-4a8a-b635-f52460163b98");
    }
}
