using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Domain.WorkItems;

namespace AkosFabric.Api.Contracts;

internal static class StatusNames
{
    public static string RepositorySession(RepositorySessionStatus status) =>
        status switch
        {
            RepositorySessionStatus.Created => "created",
            RepositorySessionStatus.Published => "published",
            RepositorySessionStatus.Starting => "starting",
            RepositorySessionStatus.Running => "running",
            RepositorySessionStatus.ProcessingResults => "processing_results",
            RepositorySessionStatus.Completed => "completed",
            RepositorySessionStatus.Failed => "failed",
            RepositorySessionStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    public static string WorkItem(WorkItemRunStatus status) =>
        status switch
        {
            WorkItemRunStatus.Queued => "queued",
            WorkItemRunStatus.Planning => "planning",
            WorkItemRunStatus.Coding => "coding",
            WorkItemRunStatus.Verifying => "verifying",
            WorkItemRunStatus.Judging => "judging",
            WorkItemRunStatus.Revising => "revising",
            WorkItemRunStatus.Accepted => "accepted",
            WorkItemRunStatus.BranchPushed => "branch_pushed",
            WorkItemRunStatus.ChangeRequestCreated => "change_request_created",
            WorkItemRunStatus.Blocked => "blocked",
            WorkItemRunStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
}
