using System.Text.Json;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Domain.Ledger;
using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.Infrastructure.Execution;

public sealed class RepositorySessionStartupReconciler
    : IRepositorySessionStartupReconciler
{
    private readonly IRepositorySessionExecutor executor;
    private readonly IRepositorySessionRepository repository;
    private readonly IRepositoryProfileProvider profileProvider;
    private readonly ISourceControlCredentialAcquisitionService
        credentialAcquisition;
    private readonly IRepositorySessionMonitorAttacher monitorAttacher;
    private readonly IAgentResultProcessor resultProcessor;
    private readonly ISessionArtifactRetentionScheduler retentionScheduler;
    private readonly RepositorySessionMonitorOptions options;
    private readonly TimeProvider timeProvider;
    private int started;

    public RepositorySessionStartupReconciler(
        IRepositorySessionExecutor executor,
        IRepositorySessionRepository repository,
        IRepositoryProfileProvider profileProvider,
        ISourceControlCredentialAcquisitionService credentialAcquisition,
        IRepositorySessionMonitorAttacher monitorAttacher,
        IAgentResultProcessor resultProcessor,
        ISessionArtifactRetentionScheduler retentionScheduler,
        RepositorySessionMonitorOptions options,
        TimeProvider? timeProvider = null)
    {
        this.executor =
            executor ?? throw new ArgumentNullException(nameof(executor));
        this.repository =
            repository ?? throw new ArgumentNullException(nameof(repository));
        this.profileProvider =
            profileProvider ??
            throw new ArgumentNullException(nameof(profileProvider));
        this.credentialAcquisition =
            credentialAcquisition ??
            throw new ArgumentNullException(nameof(credentialAcquisition));
        this.monitorAttacher =
            monitorAttacher ??
            throw new ArgumentNullException(nameof(monitorAttacher));
        this.resultProcessor =
            resultProcessor ??
            throw new ArgumentNullException(nameof(resultProcessor));
        this.retentionScheduler =
            retentionScheduler ??
            throw new ArgumentNullException(nameof(retentionScheduler));
        this.options =
            options ?? throw new ArgumentNullException(nameof(options));
        if (options.StartupScanTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The startup scan timeout must be positive.");
        }

        if (options.MaximumStartupContainers <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The startup container bound must be positive.");
        }

        if (options.CredentialRefreshSafetyMargin <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The credential refresh safety margin must be positive.");
        }

        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ReconcileOnceAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        bounded.CancelAfter(options.StartupScanTimeout);
        IReadOnlyList<RepositorySessionContainer> containers =
            await executor.ListManagedAsync(bounded.Token);
        if (containers.Count > options.MaximumStartupContainers)
        {
            throw new InvalidOperationException(
                $"Startup reconciliation found {containers.Count} managed " +
                $"containers; the configured bound is " +
                $"{options.MaximumStartupContainers}.");
        }

        var bySession = new Dictionary<Guid, RepositorySessionContainer>();
        foreach (RepositorySessionContainer container in containers)
        {
            if (!bySession.TryAdd(
                    container.RepositorySessionId,
                    container))
            {
                throw new InvalidOperationException(
                    $"More than one managed container claims session " +
                    $"'{container.RepositorySessionId}'.");
            }

            RepositorySessionRecord? session =
                await repository.FindAsync(
                    container.RepositorySessionId,
                    bounded.Token);
            if (session is null
                || session.Status is RepositorySessionStatus.Completed
                    or RepositorySessionStatus.Failed
                    or RepositorySessionStatus.Cancelled)
            {
                await CleanupOrphanAsync(
                    container.RepositorySessionId,
                    bounded.Token);
                continue;
            }

            session = await EnsureRunningAsync(
                session,
                container,
                bounded.Token);
            if (container.State == RepositorySessionContainerState.Running &&
                !await TryRefreshCredentialAsync(
                    session,
                    bounded.Token))
            {
                continue;
            }

            await monitorAttacher.AttachAsync(
                session.Id,
                bounded.Token);
        }

        IReadOnlyList<RepositorySessionRecord> recoverable =
            await repository.ListRecoverableAsync(bounded.Token);
        foreach (RepositorySessionRecord session in recoverable)
        {
            if (bySession.ContainsKey(session.Id))
            {
                continue;
            }

            RepositorySessionRecord ready = session;
            if (session.Status == RepositorySessionStatus.Starting)
            {
                ready = await MarkRunningForMissingRecoveryAsync(
                    session,
                    bounded.Token);
            }

            try
            {
                await resultProcessor.ProcessRecoveredAsync(
                    ready.Id,
                    bounded.Token);
            }
            finally
            {
                await executor.DeleteCredentialAsync(
                    ready.Id,
                    CancellationToken.None);
                await retentionScheduler.ScheduleAsync(
                    ready.Id,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None);
            }
        }
    }

    private async Task<RepositorySessionRecord> EnsureRunningAsync(
        RepositorySessionRecord session,
        RepositorySessionContainer container,
        CancellationToken cancellationToken)
    {
        if (session.Status is RepositorySessionStatus.Created
            or RepositorySessionStatus.Published)
        {
            await repository.TransitionSessionStatusAsync(
                new RepositorySessionStatusTransition(
                    session.Id,
                    session.Status,
                    RepositorySessionStatus.Starting,
                    RunLedgerEventType.SessionStarting,
                    JsonSerializer.Serialize(
                        new
                        {
                            container.ContainerName,
                            reattached = true,
                        }),
                    timeProvider.GetUtcNow(),
                    ContainerName: container.ContainerName),
                cancellationToken);
            session = session with
            {
                Status = RepositorySessionStatus.Starting,
                ContainerName = container.ContainerName,
            };
        }

        if (session.Status == RepositorySessionStatus.Starting)
        {
            await repository.TransitionSessionStatusAsync(
                new RepositorySessionStatusTransition(
                    session.Id,
                    RepositorySessionStatus.Starting,
                    RepositorySessionStatus.Running,
                    RunLedgerEventType.SessionReattached,
                    JsonSerializer.Serialize(
                        new
                        {
                            container.ContainerName,
                            container.ContainerId,
                            reattached = true,
                        }),
                    timeProvider.GetUtcNow(),
                    container.ContainerName,
                    container.ContainerId),
                cancellationToken);
            session = session with
            {
                Status = RepositorySessionStatus.Running,
                ContainerName = container.ContainerName,
                ContainerId = container.ContainerId,
                StartedAt = session.StartedAt ?? container.StartedAt,
            };
        }

        if (session.Status is RepositorySessionStatus.Running
                or RepositorySessionStatus.ProcessingResults
            && (!string.Equals(
                    session.ContainerName,
                    container.ContainerName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    session.ContainerId,
                    container.ContainerId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Startup container identity does not match the session ledger.");
        }

        return session;
    }

    private async Task<RepositorySessionRecord>
        MarkRunningForMissingRecoveryAsync(
            RepositorySessionRecord session,
            CancellationToken cancellationToken)
    {
        await repository.TransitionSessionStatusAsync(
            new RepositorySessionStatusTransition(
                session.Id,
                RepositorySessionStatus.Starting,
                RepositorySessionStatus.Running,
                RunLedgerEventType.SessionReattached,
                """{"containerMissing":true,"resultRecovery":true}""",
                timeProvider.GetUtcNow(),
                session.ContainerName,
                session.ContainerId),
            cancellationToken);
        return session with
        {
            Status = RepositorySessionStatus.Running,
            StartedAt = session.StartedAt ?? timeProvider.GetUtcNow(),
        };
    }

    private async Task CleanupOrphanAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        await executor.DeleteCredentialAsync(
            repositorySessionId,
            cancellationToken);
        await executor.RemoveAsync(
            repositorySessionId,
            cancellationToken);
        await retentionScheduler.ScheduleAsync(
            repositorySessionId,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task<bool> TryRefreshCredentialAsync(
        RepositorySessionRecord session,
        CancellationToken cancellationToken)
    {
        try
        {
            RepositoryProfile profile =
                await profileProvider.FindAsync(
                    session.RepositoryProfile,
                    cancellationToken)
                ?? throw new InvalidDataException(
                    $"Repository profile '{session.RepositoryProfile}' was not found.");
            SourceControlCredential credential =
                await credentialAcquisition.AcquireForSessionAsync(
                    session,
                    profile,
                    cancellationToken);
            if (credential.ExpiresAt?.Subtract(
                    options.CredentialRefreshSafetyMargin)
                <= timeProvider.GetUtcNow())
            {
                throw new InvalidDataException(
                    "The acquired source-control credential expires inside " +
                    "the configured refresh safety margin.");
            }

            await executor.ReplaceCredentialAsync(
                session.Id,
                credential,
                cancellationToken);
            return true;
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
            await repository.FailResultProcessingAsync(
                new AgentResultProcessingFailure(
                    session.Id,
                    "source_control_credential_refresh_failed",
                    "Refreshing the source-control credential during startup " +
                    "reconciliation failed.",
                    """{"failureCode":"source_control_credential_refresh_failed"}""",
                    timeProvider.GetUtcNow()),
                cancellationToken);
            await CleanupOrphanAsync(
                session.Id,
                cancellationToken);
            return false;
        }
    }
}
