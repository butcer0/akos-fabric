namespace AkosFabric.Application.Jira.Models;

public sealed record JiraTransitionResult(
    JiraTransitionOutcome Outcome,
    string TargetStatus,
    string? TransitionId);

public enum JiraTransitionOutcome
{
    Applied,
    Unavailable,
}

public enum JiraWorkflowTarget
{
    Assigned,
    Review,
    Completed,
    Failed,
}
