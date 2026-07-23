namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record RepositorySessionDetails(
    RepositorySessionRecord Session,
    IReadOnlyList<WorkItemRunRecord> WorkItems);
