using AkosFabric.Domain.Ledger;
using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record RepositorySessionStatusTransition(
    Guid RepositorySessionId,
    RepositorySessionStatus ExpectedStatus,
    RepositorySessionStatus NewStatus,
    RunLedgerEventType EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    string? ContainerName = null,
    string? ContainerId = null,
    string? FailureCode = null,
    string? FailureMessage = null);
