using AkosFabric.Application.RepositorySessions.Models;

namespace AkosFabric.Application.RepositorySessions.Interfaces;

public interface IRepositorySessionService
{
    Task<RepositorySessionDetails> CreateAsync(
        CreateRepositorySessionInput input,
        RepositorySessionCaller caller,
        CancellationToken cancellationToken);

    Task<RepositorySessionDetails> PublishAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);

    Task<RepositorySessionDetails> RetryAsync(
        Guid repositorySessionId,
        RepositorySessionCaller caller,
        CancellationToken cancellationToken);

    Task<RepositorySessionDetails> CancelAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);

    Task<RepositorySessionDetails> GetAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItemRunRecord>> ListItemsAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);
}
