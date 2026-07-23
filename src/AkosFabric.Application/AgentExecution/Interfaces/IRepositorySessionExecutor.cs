using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface IRepositorySessionExecutor
{
    Task<RepositorySessionExecution> StartAsync(
        RepositorySessionExecutionRequest request,
        CancellationToken cancellationToken);

    Task<RepositorySessionContainer?> InspectAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);

    Task<RepositorySessionWaitResult> WaitAsync(
        Guid repositorySessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task StreamLogsAsync(
        Guid repositorySessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task ReplaceCredentialAsync(
        Guid repositorySessionId,
        SourceControlCredential credential,
        CancellationToken cancellationToken);

    Task DeleteCredentialAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);

    Task StopAsync(Guid repositorySessionId, CancellationToken cancellationToken);

    Task RemoveAsync(Guid repositorySessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RepositorySessionContainer>> ListManagedAsync(
        CancellationToken cancellationToken);
}
