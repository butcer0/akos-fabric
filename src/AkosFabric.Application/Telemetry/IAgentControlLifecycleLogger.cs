namespace AkosFabric.Application.Telemetry;

public interface IAgentControlLifecycleLogger
{
    void Log(
        Guid repositorySessionId,
        Guid? workItemRunId,
        string lifecycleEvent,
        string provider,
        string? failureCode);
}

public sealed class NullAgentControlLifecycleLogger
    : IAgentControlLifecycleLogger
{
    public static NullAgentControlLifecycleLogger Instance { get; } =
        new();

    private NullAgentControlLifecycleLogger()
    {
    }

    public void Log(
        Guid repositorySessionId,
        Guid? workItemRunId,
        string lifecycleEvent,
        string provider,
        string? failureCode)
    {
    }
}
