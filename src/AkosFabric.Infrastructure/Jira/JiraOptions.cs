namespace AkosFabric.Infrastructure.Jira;

public sealed record JiraOptions(
    IReadOnlyDictionary<string, JiraSiteOptions> Sites);

public sealed record JiraSiteOptions(
    Uri BaseUrl,
    string AuthenticationProfile);
