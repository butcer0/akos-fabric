namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface ISessionArtifactRetentionScheduler
{
    Task ScheduleAsync(
        Guid repositorySessionId,
        DateTimeOffset sessionFinishedAt,
        CancellationToken cancellationToken);
}
