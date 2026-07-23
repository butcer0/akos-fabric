using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record RepositorySessionCancellation(
    Guid RepositorySessionId,
    RepositorySessionStatus ExpectedStatus,
    string PayloadJson,
    DateTimeOffset OccurredAt);
