using AkosFabric.Domain.Ledger;
using AkosFabric.Domain.WorkItems;

namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record WorkItemRunStatusTransition(
    Guid RepositorySessionId,
    Guid WorkItemRunId,
    WorkItemRunStatus ExpectedStatus,
    WorkItemRunStatus NewStatus,
    RunLedgerEventType EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    string? FailureCode = null,
    string? FailureMessage = null);
