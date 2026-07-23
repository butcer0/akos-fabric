namespace AkosFabric.Domain.RepositorySessions;

public enum RepositorySessionStatus
{
    Created,
    Published,
    Starting,
    Running,
    ProcessingResults,
    Completed,
    Failed,
    Cancelled,
}
