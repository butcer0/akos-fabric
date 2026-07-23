using AkosFabric.Application.AgentExecution.Models;

namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface IAgentSessionArtifactReader
{
    Task<AgentSessionArtifactsV1> ReadAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);
}
