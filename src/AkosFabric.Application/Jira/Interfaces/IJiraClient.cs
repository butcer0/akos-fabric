using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.RepositoryProfiles.Models;

namespace AkosFabric.Application.Jira.Interfaces;

public interface IJiraClient
{
    Task<IReadOnlyList<JiraIssueSnapshot>> SearchIssuesAsync(
        JiraRepositoryProfile profile,
        CancellationToken cancellationToken);

    Task<JiraIssueSnapshot?> FindIssueAsync(
        JiraRepositoryProfile profile,
        string issueKey,
        CancellationToken cancellationToken);

    Task<JiraTransitionResult> TransitionIssueAsync(
        JiraRepositoryProfile profile,
        string issueKey,
        JiraWorkflowTarget workflowTarget,
        CancellationToken cancellationToken);

    Task AddCommentAsync(
        JiraRepositoryProfile profile,
        string issueKey,
        string comment,
        CancellationToken cancellationToken);
}
