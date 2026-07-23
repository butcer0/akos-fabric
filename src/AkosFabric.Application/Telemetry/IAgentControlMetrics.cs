namespace AkosFabric.Application.Telemetry;

public interface IAgentControlMetrics
{
    void RecordRepositorySessionCreated(string sourceControlProvider);

    void RecordRepositorySessionDuration(
        string sourceControlProvider,
        string outcome,
        TimeSpan duration);

    void RecordWorkItem(
        string sourceControlProvider,
        string outcome);

    void RecordModelUsage(
        string modelProvider,
        string model,
        string role,
        long requestCount,
        long inputTokens,
        long outputTokens,
        decimal estimatedCostUsd);

    void RecordVerificationFailure(string sourceControlProvider);

    void RecordJudgeDisposition(
        string sourceControlProvider,
        string disposition);

    void RecordChangeRequestCreated(string sourceControlProvider);
}

public sealed class NullAgentControlMetrics : IAgentControlMetrics
{
    public static NullAgentControlMetrics Instance { get; } = new();

    private NullAgentControlMetrics()
    {
    }

    public void RecordRepositorySessionCreated(
        string sourceControlProvider)
    {
    }

    public void RecordRepositorySessionDuration(
        string sourceControlProvider,
        string outcome,
        TimeSpan duration)
    {
    }

    public void RecordWorkItem(
        string sourceControlProvider,
        string outcome)
    {
    }

    public void RecordModelUsage(
        string modelProvider,
        string model,
        string role,
        long requestCount,
        long inputTokens,
        long outputTokens,
        decimal estimatedCostUsd)
    {
    }

    public void RecordVerificationFailure(string sourceControlProvider)
    {
    }

    public void RecordJudgeDisposition(
        string sourceControlProvider,
        string disposition)
    {
    }

    public void RecordChangeRequestCreated(string sourceControlProvider)
    {
    }
}
