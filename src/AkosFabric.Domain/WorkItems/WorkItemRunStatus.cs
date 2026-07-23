namespace AkosFabric.Domain.WorkItems;

public enum WorkItemRunStatus
{
    Queued,
    Planning,
    Coding,
    Verifying,
    Judging,
    Revising,
    Accepted,
    BranchPushed,
    ChangeRequestCreated,
    Blocked,
    Failed,
}
