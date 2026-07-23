using AkosFabric.Application.AgentExecution.Models;

namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface IAgentResultProcessor
{
    Task ProcessAsync(
        RepositorySessionContainer container,
        CancellationToken cancellationToken);

    Task ProcessRecoveredAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);
}
