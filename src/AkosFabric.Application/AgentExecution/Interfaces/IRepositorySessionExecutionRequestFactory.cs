using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositorySessions.Models;

namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface IRepositorySessionExecutionRequestFactory
{
    Task<RepositorySessionExecutionRequest> CreateAsync(
        RepositorySessionRecord session,
        RepositorySessionRequestedV1 message,
        CancellationToken cancellationToken);
}
