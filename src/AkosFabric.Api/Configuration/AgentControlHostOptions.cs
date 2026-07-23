namespace AkosFabric.Api.Configuration;

public sealed class AgentControlHostOptions
{
    public const string SectionName = "AgentControl";

    public bool Enabled { get; init; }

    public bool MigrateDatabaseOnStart { get; init; }

    public bool ReconcileDockerOnStart { get; init; }

    public bool StartRabbitMqConsumer { get; init; }

    public bool StartJiraSelectionWorker { get; init; }

    public bool StartRetentionCleanupWorker { get; init; }

    public bool ExportTelemetry { get; init; }

    public TimeSpan RetentionCleanupInterval { get; init; } =
        TimeSpan.FromHours(1);

    public void Validate()
    {
        if (RetentionCleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(RetentionCleanupInterval)} must be positive.");
        }

        if (!Enabled &&
            (MigrateDatabaseOnStart ||
             ReconcileDockerOnStart ||
             StartRabbitMqConsumer ||
             StartJiraSelectionWorker ||
             StartRetentionCleanupWorker ||
             ExportTelemetry))
        {
            throw new InvalidOperationException(
                "AgentControl must be enabled before an external runtime " +
                "feature can be enabled.");
        }
    }
}
