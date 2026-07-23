using System.Diagnostics;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Infrastructure.Messaging;
using AkosFabric.Infrastructure.Telemetry;

namespace AkosFabric.IntegrationTests.Messaging;

public sealed class RepositorySessionDeliveryHandlerTests
{
    private static readonly Guid SessionId =
        Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c");
    private static readonly Guid MessageId =
        Guid.Parse("3631ff74-1391-4518-b6b0-ed04819ee346");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 30, 0, TimeSpan.Zero);
    private const string ContainerName =
        "agent-6a92a62a-1e93-4b5b-a52c-dcc541fb591c";
    private const string ContainerId =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(RepositorySessionStatus.Starting)]
    [InlineData(RepositorySessionStatus.Running)]
    [InlineData(RepositorySessionStatus.ProcessingResults)]
    [InlineData(RepositorySessionStatus.Completed)]
    [InlineData(RepositorySessionStatus.Failed)]
    [InlineData(RepositorySessionStatus.Cancelled)]
    public async Task DuplicateOrIneligibleDeliveryAcknowledgesWithoutLaunch(
        RepositorySessionStatus status)
    {
        var fixture = new Fixture(status);

        RepositorySessionDeliveryResult result =
            await fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None);

        Assert.Equal(
            RepositorySessionDeliveryDisposition.Acknowledge,
            result.Disposition);
        Assert.Equal(
            status is RepositorySessionStatus.Failed
                or RepositorySessionStatus.Cancelled
                ? RepositorySessionDeliveryOutcome.Ineligible
                : RepositorySessionDeliveryOutcome.Duplicate,
            result.Outcome);
        Assert.Equal(["find"], fixture.Calls);
        Assert.Empty(fixture.Repository.Transitions);
    }

    [Theory]
    [InlineData(RepositorySessionStatus.Created)]
    [InlineData(RepositorySessionStatus.Published)]
    public async Task EligibleDeliveryPersistsIdentityBeforeAcknowledgement(
        RepositorySessionStatus status)
    {
        var fixture = new Fixture(status);

        RepositorySessionDeliveryResult result =
            await fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None);

        Assert.Equal(
            RepositorySessionDeliveryResult.Acknowledge(
                RepositorySessionDeliveryOutcome.Started),
            result);
        Assert.Equal(
            [
                "find",
                "inspect",
                "transition:Starting",
                "request",
                "start",
                "transition:Running",
                "monitor",
            ],
            fixture.Calls);
        Assert.Collection(
            fixture.Repository.Transitions,
            starting =>
            {
                Assert.Equal(status, starting.ExpectedStatus);
                Assert.Equal(
                    RepositorySessionStatus.Starting,
                    starting.NewStatus);
                Assert.Equal(ContainerName, starting.ContainerName);
                Assert.Null(starting.ContainerId);
            },
            running =>
            {
                Assert.Equal(
                    RepositorySessionStatus.Starting,
                    running.ExpectedStatus);
                Assert.Equal(
                    RepositorySessionStatus.Running,
                    running.NewStatus);
                Assert.Equal(ContainerName, running.ContainerName);
                Assert.Equal(ContainerId, running.ContainerId);
            });
    }

    [Fact]
    public async Task ExistingDeterministicContainerIsReattachedWithoutStart()
    {
        var fixture = new Fixture(RepositorySessionStatus.Published);
        fixture.Executor.Container = new RepositorySessionContainer(
            SessionId,
            "akos-fabric",
            ContainerName,
            ContainerId,
            RepositorySessionContainerState.Running,
            0,
            Now,
            null);

        RepositorySessionDeliveryResult result =
            await fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None);

        Assert.Equal(
            RepositorySessionDeliveryOutcome.Reattached,
            result.Outcome);
        Assert.Equal(
            [
                "find",
                "inspect",
                "transition:Starting",
                "transition:Running",
                "monitor",
            ],
            fixture.Calls);
        Assert.Equal(
            AkosFabric.Domain.Ledger.RunLedgerEventType.SessionReattached,
            fixture.Repository.Transitions[1].EventType);
        Assert.Equal(
            ContainerId,
            fixture.Repository.Transitions[1].ContainerId);
    }

    [Fact]
    public async Task DockerStartFailureIsDurablyFailedThenAcknowledged()
    {
        var fixture = new Fixture(RepositorySessionStatus.Published);
        fixture.Executor.StartException =
            new InvalidOperationException("docker unavailable");

        RepositorySessionDeliveryResult result =
            await fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None);

        Assert.Equal(
            RepositorySessionDeliveryOutcome.StartFailed,
            result.Outcome);
        Assert.Equal(
            RepositorySessionDeliveryDisposition.Acknowledge,
            result.Disposition);
        Assert.Equal(
            RepositorySessionStatus.Failed,
            fixture.Repository.Transitions[^1].NewStatus);
        Assert.Equal(
            "agent_start_failed",
            fixture.Repository.Transitions[^1].FailureCode);
        Assert.DoesNotContain(
            "docker unavailable",
            fixture.Repository.Transitions[^1].FailureMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("monitor", fixture.Calls);
    }

    [Fact]
    public async Task RedeliveryAfterDurableRunningIsIdempotent()
    {
        var fixture = new Fixture(RepositorySessionStatus.Published);

        RepositorySessionDeliveryResult first =
            await fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None);
        RepositorySessionDeliveryResult second =
            await fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None);

        Assert.Equal(
            RepositorySessionDeliveryOutcome.Started,
            first.Outcome);
        Assert.Equal(
            RepositorySessionDeliveryOutcome.Duplicate,
            second.Outcome);
        Assert.Equal(1, fixture.Executor.StartCount);
        Assert.Equal(2, fixture.Repository.Transitions.Count);
    }

    [Fact]
    public async Task RunningTransitionFailurePreventsAcknowledgementResult()
    {
        var fixture = new Fixture(RepositorySessionStatus.Published);
        fixture.Repository.FailTransitionTo =
            RepositorySessionStatus.Running;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None));

        Assert.DoesNotContain("monitor", fixture.Calls);
    }

    [Fact]
    public async Task CancelledBeforeDeliveryAcknowledgesWithoutDockerRead()
    {
        var fixture = new Fixture(RepositorySessionStatus.Cancelled);

        RepositorySessionDeliveryResult result =
            await fixture.Handler.HandleAsync(
                Message(),
                CancellationToken.None);

        Assert.Equal(
            RepositorySessionDeliveryDisposition.Acknowledge,
            result.Disposition);
        Assert.Equal(["find"], fixture.Calls);
    }

    [Fact]
    public async Task MessageIdentityMismatchRejectsWithoutLaunch()
    {
        var fixture = new Fixture(RepositorySessionStatus.Published);
        RepositorySessionRequestedV1 wrongMessage =
            Message() with { MessageId = Guid.NewGuid() };

        RepositorySessionDeliveryResult result =
            await fixture.Handler.HandleAsync(
                wrongMessage,
                CancellationToken.None);

        Assert.Equal(
            RepositorySessionDeliveryDisposition.Reject,
            result.Disposition);
        Assert.Equal(
            RepositorySessionDeliveryOutcome.MessageIdentityMismatch,
            result.Outcome);
        Assert.Equal(["find"], fixture.Calls);
    }

    [Fact]
    public async Task ConsumerSpanIdentityIsPropagatedIntoDockerRequest()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == AgentControlTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using Activity? consume = AgentControlTelemetry.StartActivity(
            AgentControlSpans.RabbitMqConsume,
            ActivityKind.Consumer,
            AgentControlTelemetry.ParseTraceParent(
                Message().TraceParent),
            new ControlCorrelation(SessionId));
        Assert.NotNull(consume);
        var fixture = new Fixture(RepositorySessionStatus.Published);

        await fixture.Handler.HandleAsync(
            Message(),
            CancellationToken.None);

        Assert.Equal(
            consume.Id,
            fixture.RequestFactory.TraceParent);
        Assert.NotEqual(
            Message().TraceParent,
            fixture.RequestFactory.TraceParent);
    }

    private static RepositorySessionRequestedV1 Message() =>
        new(
            1,
            MessageId,
            SessionId,
            "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01",
            Now);

    private sealed class Fixture
    {
        public Fixture(RepositorySessionStatus status)
        {
            Calls = [];
            Repository = new FakeRepository(
                Session(status),
                Calls);
            Executor = new FakeExecutor(Calls);
            RequestFactory = new FakeRequestFactory(Calls);
            Monitor = new FakeMonitor(Calls);
            Handler = new RepositorySessionDeliveryHandler(
                Repository,
                Executor,
                RequestFactory,
                Monitor,
                new FixedTimeProvider(Now));
        }

        public List<string> Calls { get; }
        public FakeRepository Repository { get; }
        public FakeExecutor Executor { get; }
        public FakeRequestFactory RequestFactory { get; }
        public FakeMonitor Monitor { get; }
        public RepositorySessionDeliveryHandler Handler { get; }
    }

    private sealed class FakeRepository(
        RepositorySessionRecord session,
        List<string> calls)
        : IRepositorySessionRepository
    {
        private RepositorySessionRecord current = session;

        public List<RepositorySessionStatusTransition> Transitions { get; } =
            [];

        public RepositorySessionStatus? FailTransitionTo { get; set; }

        public Task<RepositorySessionRecord?> FindAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            calls.Add("find");
            return Task.FromResult<RepositorySessionRecord?>(
                id == current.Id ? current : null);
        }

        public Task TransitionSessionStatusAsync(
            RepositorySessionStatusTransition transition,
            CancellationToken cancellationToken)
        {
            calls.Add($"transition:{transition.NewStatus}");
            if (FailTransitionTo == transition.NewStatus)
            {
                throw new InvalidOperationException("transition failed");
            }

            Assert.Equal(current.Status, transition.ExpectedStatus);
            Transitions.Add(transition);
            current = current with
            {
                Status = transition.NewStatus,
                ContainerName =
                    transition.ContainerName ?? current.ContainerName,
                ContainerId =
                    transition.ContainerId ?? current.ContainerId,
            };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionRecord>> ListRecoverableAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task RecordJiraSynchronizationWarningAsync(
            JiraSynchronizationWarningRecording recording,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailResultProcessingAsync(
            AgentResultProcessingFailure failure,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteResultProcessingAsync(
            AgentResultProcessingCompletion completion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeExecutor(List<string> calls)
        : IRepositorySessionExecutor
    {
        public RepositorySessionContainer? Container { get; set; }
        public Exception? StartException { get; set; }
        public int StartCount { get; private set; }

        public Task<RepositorySessionContainer?> InspectAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add("inspect");
            return Task.FromResult(Container);
        }

        public Task<RepositorySessionExecution> StartAsync(
            RepositorySessionExecutionRequest request,
            CancellationToken cancellationToken)
        {
            calls.Add("start");
            StartCount++;
            if (StartException is not null)
            {
                throw StartException;
            }

            return Task.FromResult(
                new RepositorySessionExecution(
                    SessionId,
                    ContainerName,
                    ContainerId,
                    "session-dir",
                    false));
        }

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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionContainer>> ListManagedAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRequestFactory(List<string> calls)
        : IRepositorySessionExecutionRequestFactory
    {
        public string? TraceParent { get; private set; }

        public Task<RepositorySessionExecutionRequest> CreateAsync(
            RepositorySessionRecord session,
            RepositorySessionRequestedV1 message,
            CancellationToken cancellationToken)
        {
            calls.Add("request");
            TraceParent = message.TraceParent;
            return Task.FromResult(
                new RepositorySessionExecutionRequest(
                    session.Id,
                    session.RepositoryProfile,
                    "{}"u8.ToArray(),
                    new SourceControlCredential(
                        "x-access-token",
                        "secret",
                        Now.AddHours(1)),
                    session.ImageReference,
                    session.ImageDigest,
                    new Uri("http://otel.test:4317"),
                    message.TraceParent,
                    "gemini",
                    "gemini/gemini-3.6-flash",
                    "gemini-secret"));
        }
    }

    private sealed class FakeMonitor(List<string> calls)
        : IRepositorySessionMonitorAttacher
    {
        public Task AttachAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            calls.Add("monitor");
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static RepositorySessionRecord Session(
        RepositorySessionStatus status) =>
        new(
            SessionId,
            "akos-fabric",
            new string('a', 40),
            "github",
            "akos-fabric-agent:1.4",
            $"sha256:{new string('b', 64)}",
            status,
            MessageId,
            null,
            null,
            "{}",
            "subject",
            "client",
            null,
            null,
            Now,
            status == RepositorySessionStatus.Published ? Now : null,
            null,
            null,
            null,
            null);
}
