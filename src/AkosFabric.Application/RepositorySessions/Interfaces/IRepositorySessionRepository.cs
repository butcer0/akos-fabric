using AkosFabric.Application.RepositorySessions.Models;

namespace AkosFabric.Application.RepositorySessions.Interfaces;

public interface IRepositorySessionRepository
{
    Task<RepositorySessionRecord?> FindAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RepositorySessionRecord>> ListRecoverableAsync(
        CancellationToken cancellationToken);

    Task CreateAsync(
        RepositorySessionCreation session,
        CancellationToken cancellationToken);

    Task CancelAsync(
        RepositorySessionCancellation cancellation,
        CancellationToken cancellationToken);

    Task TransitionSessionStatusAsync(
        RepositorySessionStatusTransition transition,
        CancellationToken cancellationToken);

    Task TransitionWorkItemStatusAsync(
        WorkItemRunStatusTransition transition,
        CancellationToken cancellationToken);

    Task RecordValidatedResultAsync(
        AgentResultRecording result,
        CancellationToken cancellationToken);

    Task RecordChangeRequestAsync(
        ChangeRequestRecording recording,
        CancellationToken cancellationToken);

    Task RecordJiraSynchronizationWarningAsync(
        JiraSynchronizationWarningRecording recording,
        CancellationToken cancellationToken);

    Task FailResultProcessingAsync(
        AgentResultProcessingFailure failure,
        CancellationToken cancellationToken);

    Task CompleteResultProcessingAsync(
        AgentResultProcessingCompletion completion,
        CancellationToken cancellationToken);
}
