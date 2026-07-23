namespace AkosFabric.Domain.Ledger;

public sealed record RunLedgerEvent(
    Guid RepositorySessionId,
    Guid? WorkItemRunId,
    RunLedgerEventType EventType,
    DateTimeOffset OccurredAt,
    string PayloadJson);
