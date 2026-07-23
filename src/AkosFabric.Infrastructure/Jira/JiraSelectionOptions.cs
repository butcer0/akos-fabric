namespace AkosFabric.Infrastructure.Jira;

public sealed class JiraSelectionOptions
{
    public int PollingIntervalSeconds { get; init; } = 300;

    public string[] EnabledRepositoryProfiles { get; init; } = [];

    public void Validate()
    {
        if (PollingIntervalSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PollingIntervalSeconds)} must be greater than zero.");
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? profile in EnabledRepositoryProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile))
            {
                throw new InvalidOperationException(
                    $"{nameof(EnabledRepositoryProfiles)} cannot contain " +
                    "an empty profile name.");
            }

            if (!unique.Add(profile.Trim()))
            {
                throw new InvalidOperationException(
                    $"Repository profile '{profile.Trim()}' is enabled more " +
                    "than once.");
            }
        }
    }
}
