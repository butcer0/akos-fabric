namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface IRepositorySessionStartupReconciler
{
    Task ReconcileOnceAsync(CancellationToken cancellationToken);
}
