namespace AkosFabric.Domain.Ledger;

public enum RunLedgerEventType
{
    SessionCreated,
    SessionPublished,
    SessionStarting,
    SessionStarted,
    SessionReattached,
    SessionCompleted,
    SessionFailed,
    SessionCancelled,
    ItemStarted,
    BaseCommitResolved,
    PlanCompleted,
    CodingCompleted,
    VerificationCompleted,
    CandidateCommitted,
    JudgmentCompleted,
    RevisionStarted,
    BranchPushed,
    ChangeRequestCreated,
    ItemBlocked,
    ItemFailed,
    JiraSynchronizationWarning,
}
