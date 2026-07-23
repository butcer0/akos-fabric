using System.Collections.Frozen;

namespace AkosFabric.Domain.WorkItems;

public sealed class WorkItemRun
{
    private static readonly FrozenDictionary<WorkItemRunStatus, FrozenSet<WorkItemRunStatus>>
        AllowedTransitions =
            new Dictionary<WorkItemRunStatus, FrozenSet<WorkItemRunStatus>>
            {
                [WorkItemRunStatus.Queued] = Set(
                    WorkItemRunStatus.Planning,
                    WorkItemRunStatus.Blocked,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.Planning] = Set(
                    WorkItemRunStatus.Coding,
                    WorkItemRunStatus.Blocked,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.Coding] = Set(
                    WorkItemRunStatus.Verifying,
                    WorkItemRunStatus.Blocked,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.Verifying] = Set(
                    WorkItemRunStatus.Judging,
                    WorkItemRunStatus.Blocked,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.Judging] = Set(
                    WorkItemRunStatus.Revising,
                    WorkItemRunStatus.Accepted,
                    WorkItemRunStatus.Blocked,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.Revising] = Set(
                    WorkItemRunStatus.Verifying,
                    WorkItemRunStatus.Blocked,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.Accepted] = Set(
                    WorkItemRunStatus.BranchPushed,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.BranchPushed] = Set(
                    WorkItemRunStatus.ChangeRequestCreated,
                    WorkItemRunStatus.Failed),
                [WorkItemRunStatus.ChangeRequestCreated] = Set(),
                [WorkItemRunStatus.Blocked] = Set(),
                [WorkItemRunStatus.Failed] = Set(),
            }.ToFrozenDictionary();

    public WorkItemRun(Guid id, int sequenceNumber, string jiraIssueId, string jiraKey)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A work-item run ID is required.", nameof(id));
        }

        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "Sequence numbers are one-based.");
        }

        if (string.IsNullOrWhiteSpace(jiraIssueId))
        {
            throw new ArgumentException("A Jira issue ID is required.", nameof(jiraIssueId));
        }

        if (string.IsNullOrWhiteSpace(jiraKey))
        {
            throw new ArgumentException("A Jira issue key is required.", nameof(jiraKey));
        }

        Id = id;
        SequenceNumber = sequenceNumber;
        JiraIssueId = jiraIssueId;
        JiraKey = jiraKey;
        Status = WorkItemRunStatus.Queued;
    }

    public Guid Id { get; }

    public int SequenceNumber { get; }

    public string JiraIssueId { get; }

    public string JiraKey { get; }

    public WorkItemRunStatus Status { get; private set; }

    public bool IsTerminal =>
        Status is WorkItemRunStatus.ChangeRequestCreated
            or WorkItemRunStatus.Blocked
            or WorkItemRunStatus.Failed;

    public void TransitionTo(WorkItemRunStatus nextStatus)
    {
        if (!AllowedTransitions[Status].Contains(nextStatus))
        {
            throw new InvalidOperationException(
                $"Work-item run cannot transition from {Status} to {nextStatus}.");
        }

        Status = nextStatus;
    }

    private static FrozenSet<WorkItemRunStatus> Set(params WorkItemRunStatus[] statuses) =>
        statuses.ToFrozenSet();
}
