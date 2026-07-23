namespace AkosFabric.Application.Jira.Interfaces;

public interface IJiraSelectionRepository
{
    Task<bool> HasActiveRepositorySessionAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListNonTerminalJiraKeysAsync(
        CancellationToken cancellationToken);
}
