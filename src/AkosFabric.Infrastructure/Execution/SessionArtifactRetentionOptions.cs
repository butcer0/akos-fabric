namespace AkosFabric.Infrastructure.Execution;

public sealed class SessionArtifactRetentionOptions
{
    public TimeSpan RetentionPeriod { get; init; } =
        TimeSpan.FromDays(7);

    internal void Validate()
    {
        if (RetentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetentionPeriod),
                "The session-artifact retention period must be positive.");
        }
    }
}
