using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Application.Telemetry;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Infrastructure.Telemetry;

namespace AkosFabric.Infrastructure.Execution;

public sealed class ContainerCompletionMonitor
    : IRepositorySessionMonitorAttacher
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> monitors = new();
    private readonly IRepositorySessionExecutor executor;
    private readonly IRepositorySessionRepository repository;
    private readonly IRepositoryProfileProvider profileProvider;
    private readonly IAgentResultProcessor resultProcessor;
    private readonly ISessionArtifactRetentionScheduler retentionScheduler;
    private readonly ISourceControlCredentialAcquisitionService
        credentialAcquisition;
    private readonly RepositorySessionMonitorOptions options;
    private readonly TimeProvider timeProvider;
    private readonly CancellationToken applicationStopping;
    private readonly IAgentControlMetrics metrics;
    private readonly IAgentControlLifecycleLogger lifecycleLogger;

    public ContainerCompletionMonitor(
        IRepositorySessionExecutor executor,
        IRepositorySessionRepository repository,
        IRepositoryProfileProvider profileProvider,
        IAgentResultProcessor resultProcessor,
        ISessionArtifactRetentionScheduler retentionScheduler,
        ISourceControlCredentialAcquisitionService credentialAcquisition,
        RepositorySessionMonitorOptions options,
        TimeProvider? timeProvider = null,
        CancellationToken applicationStopping = default)
        : this(
            executor,
            repository,
            profileProvider,
            resultProcessor,
            retentionScheduler,
            credentialAcquisition,
            options,
            NullAgentControlMetrics.Instance,
            NullAgentControlLifecycleLogger.Instance,
            timeProvider,
            applicationStopping)
    {
    }

    public ContainerCompletionMonitor(
        IRepositorySessionExecutor executor,
        IRepositorySessionRepository repository,
        IRepositoryProfileProvider profileProvider,
        IAgentResultProcessor resultProcessor,
        ISessionArtifactRetentionScheduler retentionScheduler,
        ISourceControlCredentialAcquisitionService credentialAcquisition,
        RepositorySessionMonitorOptions options,
        IAgentControlMetrics metrics,
        IAgentControlLifecycleLogger lifecycleLogger,
        TimeProvider? timeProvider = null,
        CancellationToken applicationStopping = default)
    {
        this.executor =
            executor ?? throw new ArgumentNullException(nameof(executor));
        this.repository =
            repository ?? throw new ArgumentNullException(nameof(repository));
        this.profileProvider =
            profileProvider ??
            throw new ArgumentNullException(nameof(profileProvider));
        this.resultProcessor =
            resultProcessor ??
            throw new ArgumentNullException(nameof(resultProcessor));
        this.retentionScheduler =
            retentionScheduler ??
            throw new ArgumentNullException(nameof(retentionScheduler));
        this.credentialAcquisition =
            credentialAcquisition ??
            throw new ArgumentNullException(nameof(credentialAcquisition));
        this.options =
            options ?? throw new ArgumentNullException(nameof(options));
        if (options.CredentialRefreshSafetyMargin <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The credential refresh safety margin must be positive.");
        }

        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.applicationStopping = applicationStopping;
        this.metrics =
            metrics ?? throw new ArgumentNullException(nameof(metrics));
        this.lifecycleLogger = lifecycleLogger
            ?? throw new ArgumentNullException(nameof(lifecycleLogger));
    }

    public Task AttachAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        if (repositorySessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A repository-session ID is required.",
                nameof(repositorySessionId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Lazy<Task> monitor = monitors.GetOrAdd(
            repositorySessionId,
            id => new Lazy<Task>(
                () => RunAttachedAsync(id),
                LazyThreadSafetyMode.ExecutionAndPublication));
        _ = monitor.Value;
        return Task.CompletedTask;
    }

    public async Task MonitorAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.RepositorySessionComplete,
            correlation: new ControlCorrelation(repositorySessionId));
        RepositorySessionRecord session =
            await repository.FindAsync(
                repositorySessionId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Repository session '{repositorySessionId}' was not found.");
        if (session.Status is RepositorySessionStatus.Completed
            or RepositorySessionStatus.Failed
            or RepositorySessionStatus.Cancelled)
        {
            await CleanupAsync(
                repositorySessionId,
                cancellationToken);
            return;
        }

        if (session.Status is not RepositorySessionStatus.Running
            and not RepositorySessionStatus.ProcessingResults)
        {
            throw new InvalidOperationException(
                $"Repository session '{repositorySessionId}' cannot be " +
                $"monitored while in status '{session.Status}'.");
        }

        RepositoryProfile profile =
            await profileProvider.FindAsync(
                session.RepositoryProfile,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Repository profile '{session.RepositoryProfile}' was not found.");
        RepositorySessionContainer? container =
            await executor.InspectAsync(
                repositorySessionId,
                cancellationToken);

        bool cleanup = true;
        try
        {
            if (container is null)
            {
                await resultProcessor.ProcessRecoveredAsync(
                    repositorySessionId,
                    cancellationToken);
                return;
            }

            ValidateContainer(session, container);
            DateTimeOffset? startedAt =
                Earliest(session.StartedAt, container.StartedAt);
            if (startedAt is null)
            {
                await FailAsync(
                    session,
                    "container_start_time_missing",
                    "The running container has no durable start time.",
                    cancellationToken);
                return;
            }

            DateTimeOffset deadline = startedAt.Value.AddMinutes(
                profile.Session.MaxDurationMinutes);
            DateTimeOffset observedNow = timeProvider.GetUtcNow();
            TimeSpan remaining = deadline - observedNow;
            if (remaining <= TimeSpan.Zero)
            {
                await StopAndFailDeadlineAsync(
                    session,
                    deadline,
                    cancellationToken);
                return;
            }

            DateTimeOffset? credentialRefreshAt = null;
            if (container.State == RepositorySessionContainerState.Running)
            {
                CredentialRefreshAttempt initialRefresh =
                    await TryRefreshCredentialAsync(
                        session,
                        profile,
                        cancellationToken);
                if (!initialRefresh.Succeeded)
                {
                    return;
                }

                credentialRefreshAt = initialRefresh.RefreshAt;
            }

            Task logs = ObserveLogsAsync(
                repositorySessionId,
                remaining,
                cancellationToken);
            RepositorySessionWaitResult? waitResult = null;
            try
            {
                while (waitResult is null)
                {
                    observedNow = Later(
                        observedNow,
                        timeProvider.GetUtcNow());
                    remaining = deadline - observedNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        await StopAndFailDeadlineAsync(
                            session,
                            deadline,
                            cancellationToken);
                        return;
                    }

                    if (credentialRefreshAt <= observedNow)
                    {
                        CredentialRefreshAttempt refresh =
                            await TryRefreshCredentialAsync(
                                session,
                                profile,
                                cancellationToken);
                        if (!refresh.Succeeded)
                        {
                            return;
                        }

                        credentialRefreshAt = refresh.RefreshAt;
                        continue;
                    }

                    TimeSpan waitSegment = credentialRefreshAt is null
                        ? remaining
                        : Min(
                            remaining,
                            credentialRefreshAt.Value - observedNow);
                    DateTimeOffset segmentEnd =
                        observedNow.Add(waitSegment);
                    RepositorySessionWaitResult segmentResult =
                        await executor.WaitAsync(
                            repositorySessionId,
                            waitSegment,
                            cancellationToken);
                    if (!segmentResult.TimedOut)
                    {
                        waitResult = segmentResult;
                        continue;
                    }

                    observedNow = Later(
                        segmentEnd,
                        timeProvider.GetUtcNow());
                    if (observedNow >= deadline)
                    {
                        await StopAndFailDeadlineAsync(
                            session,
                            deadline,
                            cancellationToken);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                RepositorySessionContainer? afterFailure =
                    await executor.InspectAsync(
                        repositorySessionId,
                        cancellationToken);
                if (afterFailure is null)
                {
                    await resultProcessor.ProcessRecoveredAsync(
                        repositorySessionId,
                        cancellationToken);
                    return;
                }

                await FailAsync(
                    session,
                    "container_wait_failed",
                    "Waiting for the agent container failed.",
                    cancellationToken);
                return;
            }
            finally
            {
                await logs;
            }

            RepositorySessionContainer? exited =
                await executor.InspectAsync(
                    repositorySessionId,
                    cancellationToken);
            if (exited is null
                || exited.State == RepositorySessionContainerState.Dead)
            {
                await resultProcessor.ProcessRecoveredAsync(
                    repositorySessionId,
                    cancellationToken);
                return;
            }

            if (exited.State != RepositorySessionContainerState.Exited)
            {
                await FailAsync(
                    session,
                    "container_not_exited",
                    $"Container wait completed but state is '{exited.State}'.",
                    cancellationToken);
                return;
            }

            await resultProcessor.ProcessAsync(
                exited,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            cleanup = false;
            throw;
        }
        finally
        {
            if (cleanup)
            {
                await CleanupAsync(
                    repositorySessionId,
                    CancellationToken.None);
            }
        }
    }

    private async Task RunAttachedAsync(Guid repositorySessionId)
    {
        try
        {
            await MonitorAsync(
                repositorySessionId,
                applicationStopping);
        }
        catch (OperationCanceledException)
            when (applicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The valid result remains retained for explicit reprocessing or
            // the next single startup reconciliation. A hosted adapter can
            // observe this task and emit telemetry without changing workflow.
        }
        finally
        {
            monitors.TryRemove(repositorySessionId, out _);
        }
    }

    private async Task StopAndFailDeadlineAsync(
        RepositorySessionRecord session,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        await executor.StopAsync(session.Id, cancellationToken);
        await FailAsync(
            session,
            "session_deadline_exceeded",
            $"Agent session exceeded its deadline at {deadline:O}.",
            cancellationToken);
    }

    private async Task<CredentialRefreshAttempt> TryRefreshCredentialAsync(
        RepositorySessionRecord session,
        RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            SourceControlCredential credential =
                await credentialAcquisition.AcquireForSessionAsync(
                    session,
                    profile,
                    cancellationToken);
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset? refreshAt = credential.ExpiresAt?.Subtract(
                options.CredentialRefreshSafetyMargin);
            if (refreshAt <= now)
            {
                throw new InvalidDataException(
                    "The acquired source-control credential expires inside " +
                    "the configured refresh safety margin.");
            }

            await executor.ReplaceCredentialAsync(
                session.Id,
                credential,
                cancellationToken);
            return new CredentialRefreshAttempt(true, refreshAt);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await executor.StopAsync(
                session.Id,
                cancellationToken);
            await FailAsync(
                session,
                "source_control_credential_refresh_failed",
                "Refreshing the source-control credential failed.",
                cancellationToken);
            return new CredentialRefreshAttempt(false, null);
        }
    }

    private async Task FailAsync(
        RepositorySessionRecord session,
        string failureCode,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        DateTimeOffset occurredAt = timeProvider.GetUtcNow();
        await repository.FailResultProcessingAsync(
            new AgentResultProcessingFailure(
                session.Id,
                failureCode,
                failureMessage,
                JsonSerializer.Serialize(
                    new
                    {
                        failureCode,
                        failureMessage,
                    }),
                occurredAt),
            cancellationToken);
        if (session.StartedAt is DateTimeOffset startedAt
            && occurredAt >= startedAt)
        {
            metrics.RecordRepositorySessionDuration(
                session.SourceControlProvider,
                "failed",
                occurredAt - startedAt);
        }

        lifecycleLogger.Log(
            session.Id,
            workItemRunId: null,
            "session_failed",
            session.SourceControlProvider,
            failureCode);
    }

    private async Task ObserveLogsAsync(
        Guid repositorySessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await executor.StreamLogsAsync(
                repositorySessionId,
                timeout,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Log streaming is diagnostic and cannot suppress result handling.
        }
    }

    private async Task CleanupAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        Exception? firstFailure = null;
        try
        {
            await executor.DeleteCredentialAsync(
                repositorySessionId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        try
        {
            await executor.RemoveAsync(
                repositorySessionId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        try
        {
            await retentionScheduler.ScheduleAsync(
                repositorySessionId,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private static void ValidateContainer(
        RepositorySessionRecord session,
        RepositorySessionContainer container)
    {
        if (container.RepositorySessionId != session.Id
            || !string.Equals(
                container.RepositoryProfile,
                session.RepositoryProfile,
                StringComparison.Ordinal)
            || !string.Equals(
                container.ContainerName,
                session.ContainerName,
                StringComparison.Ordinal)
            || !string.Equals(
                container.ContainerId,
                session.ContainerId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The managed container identity does not match the session ledger.");
        }
    }

    private static DateTimeOffset? Earliest(
        DateTimeOffset? first,
        DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first <= second ? first : second;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private static DateTimeOffset Later(
        DateTimeOffset first,
        DateTimeOffset second) =>
        first >= second ? first : second;

    private readonly record struct CredentialRefreshAttempt(
        bool Succeeded,
        DateTimeOffset? RefreshAt);
}
