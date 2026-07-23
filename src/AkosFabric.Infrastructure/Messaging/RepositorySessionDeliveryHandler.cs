using System.Diagnostics;
using System.Text.Json;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Domain.Ledger;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.Infrastructure.Messaging;

public sealed class RepositorySessionDeliveryHandler
{
    private readonly IRepositorySessionRepository repository;
    private readonly IRepositorySessionExecutor executor;
    private readonly IRepositorySessionExecutionRequestFactory requestFactory;
    private readonly IRepositorySessionMonitorAttacher monitorAttacher;
    private readonly TimeProvider timeProvider;

    public RepositorySessionDeliveryHandler(
        IRepositorySessionRepository repository,
        IRepositorySessionExecutor executor,
        IRepositorySessionExecutionRequestFactory requestFactory,
        IRepositorySessionMonitorAttacher monitorAttacher,
        TimeProvider? timeProvider = null)
    {
        this.repository =
            repository ?? throw new ArgumentNullException(nameof(repository));
        this.executor =
            executor ?? throw new ArgumentNullException(nameof(executor));
        this.requestFactory =
            requestFactory ??
            throw new ArgumentNullException(nameof(requestFactory));
        this.monitorAttacher =
            monitorAttacher ??
            throw new ArgumentNullException(nameof(monitorAttacher));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RepositorySessionDeliveryResult> HandleAsync(
        RepositorySessionRequestedV1 message,
        CancellationToken cancellationToken)
    {
        RepositorySessionMessageCodec.Validate(message);

        RepositorySessionRecord? session = await repository.FindAsync(
            message.RepositorySessionId,
            cancellationToken);
        if (session is null)
        {
            return RepositorySessionDeliveryResult.Acknowledge(
                RepositorySessionDeliveryOutcome.SessionNotFound);
        }

        if (session.MessageId != message.MessageId)
        {
            return RepositorySessionDeliveryResult.Reject(
                RepositorySessionDeliveryOutcome.MessageIdentityMismatch);
        }

        if (session.Status is RepositorySessionStatus.Starting
            or RepositorySessionStatus.Running
            or RepositorySessionStatus.ProcessingResults
            or RepositorySessionStatus.Completed)
        {
            return RepositorySessionDeliveryResult.Acknowledge(
                RepositorySessionDeliveryOutcome.Duplicate);
        }

        if (session.Status is not (
                RepositorySessionStatus.Published
                or RepositorySessionStatus.Created))
        {
            return RepositorySessionDeliveryResult.Acknowledge(
                RepositorySessionDeliveryOutcome.Ineligible);
        }

        string containerName =
            DockerRepositorySessionExecutor.GetContainerName(session.Id);
        RepositorySessionContainer? existing =
            await executor.InspectAsync(session.Id, cancellationToken);
        if (existing is not null)
        {
            ValidateContainer(existing, session.Id, containerName);
            await MarkStartingAsync(
                session,
                containerName,
                cancellationToken);
            await MarkRunningAsync(
                session.Id,
                containerName,
                existing.ContainerId,
                RunLedgerEventType.SessionReattached,
                reattached: true,
                cancellationToken);
            await monitorAttacher.AttachAsync(
                session.Id,
                cancellationToken);
            return RepositorySessionDeliveryResult.Acknowledge(
                RepositorySessionDeliveryOutcome.Reattached);
        }

        await MarkStartingAsync(
            session,
            containerName,
            cancellationToken);

        RepositorySessionExecution execution;
        try
        {
            RepositorySessionRequestedV1 executionMessage =
                Activity.Current?.Id is string currentTraceParent
                    ? message with { TraceParent = currentTraceParent }
                    : message;
            RepositorySessionExecutionRequest request =
                await requestFactory.CreateAsync(
                    session,
                    executionMessage,
                    cancellationToken);
            execution = await executor.StartAsync(
                request,
                cancellationToken);
            ValidateExecution(execution, session.Id, containerName);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await repository.TransitionSessionStatusAsync(
                new RepositorySessionStatusTransition(
                    session.Id,
                    RepositorySessionStatus.Starting,
                    RepositorySessionStatus.Failed,
                    RunLedgerEventType.SessionFailed,
                    """{"failureCode":"agent_start_failed"}""",
                    timeProvider.GetUtcNow(),
                    ContainerName: containerName,
                    FailureCode: "agent_start_failed",
                    FailureMessage: "Agent container start failed."),
                cancellationToken);
            return RepositorySessionDeliveryResult.Acknowledge(
                RepositorySessionDeliveryOutcome.StartFailed);
        }

        await MarkRunningAsync(
            session.Id,
            containerName,
            execution.ContainerId,
            execution.Reattached
                ? RunLedgerEventType.SessionReattached
                : RunLedgerEventType.SessionStarted,
            execution.Reattached,
            cancellationToken);
        await monitorAttacher.AttachAsync(
            session.Id,
            cancellationToken);
        return RepositorySessionDeliveryResult.Acknowledge(
            execution.Reattached
                ? RepositorySessionDeliveryOutcome.Reattached
                : RepositorySessionDeliveryOutcome.Started);
    }

    private Task MarkStartingAsync(
        RepositorySessionRecord session,
        string containerName,
        CancellationToken cancellationToken) =>
        repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                session.Id,
                session.Status,
                RepositorySessionStatus.Starting,
                RunLedgerEventType.SessionStarting,
                JsonSerializer.Serialize(
                    new { containerName, session.MessageId }),
                timeProvider.GetUtcNow(),
                ContainerName: containerName),
            cancellationToken);

    private Task MarkRunningAsync(
        Guid repositorySessionId,
        string containerName,
        string containerId,
        RunLedgerEventType eventType,
        bool reattached,
        CancellationToken cancellationToken) =>
        repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                repositorySessionId,
                RepositorySessionStatus.Starting,
                RepositorySessionStatus.Running,
                eventType,
                JsonSerializer.Serialize(
                    new { containerName, containerId, reattached }),
                timeProvider.GetUtcNow(),
                containerName,
                containerId),
            cancellationToken);

    private static void ValidateExecution(
        RepositorySessionExecution execution,
        Guid repositorySessionId,
        string expectedContainerName)
    {
        if (execution.RepositorySessionId != repositorySessionId ||
            !string.Equals(
                execution.ContainerName,
                expectedContainerName,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(execution.ContainerId))
        {
            throw new InvalidDataException(
                "The agent executor returned an invalid container identity.");
        }
    }

    private static void ValidateContainer(
        RepositorySessionContainer container,
        Guid repositorySessionId,
        string expectedContainerName)
    {
        if (container.RepositorySessionId != repositorySessionId ||
            !string.Equals(
                container.ContainerName,
                expectedContainerName,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(container.ContainerId))
        {
            throw new InvalidDataException(
                "The existing Docker container identity is invalid.");
        }
    }
}

public sealed record RepositorySessionDeliveryResult(
    RepositorySessionDeliveryDisposition Disposition,
    RepositorySessionDeliveryOutcome Outcome)
{
    internal static RepositorySessionDeliveryResult Acknowledge(
        RepositorySessionDeliveryOutcome outcome) =>
        new(RepositorySessionDeliveryDisposition.Acknowledge, outcome);

    internal static RepositorySessionDeliveryResult Reject(
        RepositorySessionDeliveryOutcome outcome) =>
        new(RepositorySessionDeliveryDisposition.Reject, outcome);
}

public enum RepositorySessionDeliveryDisposition
{
    Acknowledge,
    Reject,
}

public enum RepositorySessionDeliveryOutcome
{
    Started,
    Reattached,
    StartFailed,
    Duplicate,
    Ineligible,
    SessionNotFound,
    MessageIdentityMismatch,
}
