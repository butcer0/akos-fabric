namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface IRepositorySessionMonitorAttacher
{
    Task AttachAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken);
}
