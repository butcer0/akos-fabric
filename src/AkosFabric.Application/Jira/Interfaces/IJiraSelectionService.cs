using AkosFabric.Application.Jira.Models;

namespace AkosFabric.Application.Jira.Interfaces;

public interface IJiraSelectionService
{
    Task<JiraSelectionResult> SelectAsync(
        IReadOnlyList<string> enabledRepositoryProfiles,
        CancellationToken cancellationToken);
}
