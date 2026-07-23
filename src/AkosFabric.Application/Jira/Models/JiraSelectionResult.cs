namespace AkosFabric.Application.Jira.Models;

public enum JiraSelectionOutcome
{
    ActiveSession,
    NoCandidates,
    SessionCreated,
}

public sealed record JiraSelectionResult(
    JiraSelectionOutcome Outcome,
    int ProfilesQueried,
    int EligibleCandidateCount,
    string? RepositoryProfile,
    Guid? RepositorySessionId);
