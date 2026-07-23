using System.Diagnostics;
using System.Text.RegularExpressions;
using AkosFabric.Application.Telemetry;
using Microsoft.Extensions.Logging;

namespace AkosFabric.Infrastructure.Telemetry;

public sealed partial class AgentControlLifecycleLogger
    : IAgentControlLifecycleLogger
{
    private readonly ILogger<AgentControlLifecycleLogger> logger;

    public AgentControlLifecycleLogger(
        ILogger<AgentControlLifecycleLogger> logger)
    {
        this.logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Log(
        Guid repositorySessionId,
        Guid? workItemRunId,
        string lifecycleEvent,
        string provider,
        string? failureCode)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        string traceId =
            Activity.Current?.TraceId.ToHexString() ?? string.Empty;
        string safeLifecycleEvent = SanitizeToken(lifecycleEvent);
        string safeProvider = SanitizeToken(provider);
        string? safeFailureCode = failureCode is null
            ? null
            : SanitizeToken(failureCode);
        LogLifecycle(
            logger,
            traceId,
            repositorySessionId,
            workItemRunId,
            safeLifecycleEvent,
            safeProvider,
            safeFailureCode);
    }

    private static string SanitizeToken(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && MetadataToken().IsMatch(value)
            ? value
            : "invalid_metadata";

    [GeneratedRegex(
        @"\A[A-Za-z0-9][A-Za-z0-9_.-]*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex MetadataToken();

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Information,
        Message =
            "Agent lifecycle event. TraceId={TraceId} " +
            "RepositorySessionId={RepositorySessionId} " +
            "WorkItemRunId={WorkItemRunId} " +
            "LifecycleEvent={LifecycleEvent} Provider={Provider} " +
            "FailureCode={FailureCode}")]
    private static partial void LogLifecycle(
        ILogger logger,
        string traceId,
        Guid repositorySessionId,
        Guid? workItemRunId,
        string lifecycleEvent,
        string provider,
        string? failureCode);
}
