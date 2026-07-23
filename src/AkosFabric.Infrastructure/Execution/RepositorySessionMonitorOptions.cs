namespace AkosFabric.Infrastructure.Execution;

public sealed class RepositorySessionMonitorOptions
{
    public TimeSpan StartupScanTimeout { get; init; } =
        TimeSpan.FromSeconds(30);

    public int MaximumStartupContainers { get; init; } = 100;

    public TimeSpan CredentialRefreshSafetyMargin { get; init; } =
        TimeSpan.FromMinutes(5);
}
