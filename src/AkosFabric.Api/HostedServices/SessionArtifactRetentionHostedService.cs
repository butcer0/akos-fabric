using AkosFabric.Api.Configuration;
using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.Api.HostedServices;

internal sealed partial class SessionArtifactRetentionHostedService
    : BackgroundService
{
    private readonly AgentControlHostOptions options;
    private readonly SessionArtifactRetentionCleaner cleaner;
    private readonly ILogger<SessionArtifactRetentionHostedService> logger;
    private readonly TimeProvider timeProvider;

    public SessionArtifactRetentionHostedService(
        AgentControlHostOptions options,
        SessionArtifactRetentionCleaner cleaner,
        ILogger<SessionArtifactRetentionHostedService> logger,
        TimeProvider timeProvider)
    {
        this.options =
            options ?? throw new ArgumentNullException(nameof(options));
        this.cleaner =
            cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        this.logger =
            logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SessionArtifactCleanupResult result =
                    await cleaner.CleanupAsync(
                        dryRun: false,
                        stoppingToken);
                LogCleanup(
                    logger,
                    result.Candidates.Count,
                    result.DeletedSessionIds.Count);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                LogCleanupFailure(
                    logger,
                    "session_artifact_retention_failed");
            }

            try
            {
                await Task.Delay(
                    options.RetentionCleanupInterval,
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(
        EventId = 1601,
        Level = LogLevel.Information,
        Message =
            "Session-artifact retention evaluated {CandidateCount} session " +
            "directories and deleted {DeletedCount}.")]
    private static partial void LogCleanup(
        ILogger logger,
        int candidateCount,
        int deletedCount);

    [LoggerMessage(
        EventId = 1602,
        Level = LogLevel.Error,
        Message =
            "Session-artifact retention failed. The worker will retry on " +
            "the next configured interval. FailureCode={FailureCode}")]
    private static partial void LogCleanupFailure(
        ILogger logger,
        string failureCode);
}
