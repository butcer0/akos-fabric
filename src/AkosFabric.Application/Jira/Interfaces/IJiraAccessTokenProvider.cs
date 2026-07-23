namespace AkosFabric.Application.Jira.Interfaces;

public interface IJiraAccessTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(
        string authenticationProfile,
        CancellationToken cancellationToken);
}
