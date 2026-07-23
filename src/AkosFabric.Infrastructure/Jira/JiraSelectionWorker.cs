using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AkosFabric.Infrastructure.Jira;

public sealed partial class JiraSelectionWorker : BackgroundService
{
    private readonly JiraSelectionOptions options;
    private readonly IJiraSelectionService selectionService;
    private readonly ILogger<JiraSelectionWorker> logger;
    private readonly TimeProvider timeProvider;

    public JiraSelectionWorker(
        JiraSelectionOptions options,
        IJiraSelectionService selectionService,
        ILogger<JiraSelectionWorker> logger,
        TimeProvider? timeProvider = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        this.selectionService = selectionService
            ?? throw new ArgumentNullException(nameof(selectionService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan pollingInterval =
            TimeSpan.FromSeconds(options.PollingIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                JiraSelectionResult result =
                    await selectionService.SelectAsync(
                        options.EnabledRepositoryProfiles,
                        stoppingToken);
                LogResult(result);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                LogPollFailure(
                    logger,
                    "jira_selection_poll_failed");
            }

            try
            {
                await Task.Delay(
                    pollingInterval,
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

    private void LogResult(JiraSelectionResult result)
    {
        if (result.Outcome == JiraSelectionOutcome.SessionCreated)
        {
            LogSessionCreated(
                logger,
                result.RepositorySessionId,
                result.RepositoryProfile,
                result.ProfilesQueried);
            return;
        }

        LogPollResult(
            logger,
            result.Outcome,
            result.ProfilesQueried,
            result.EligibleCandidateCount);
    }

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Error,
        Message =
            "The scheduled Jira selection poll failed. The worker will retry " +
            "on the next configured interval. FailureCode={FailureCode}")]
    private static partial void LogPollFailure(
        ILogger logger,
        string failureCode);

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Information,
        Message =
            "Scheduled Jira selection created repository session " +
            "{RepositorySessionId} for profile {RepositoryProfile} after " +
            "querying {ProfilesQueried} enabled profiles.")]
    private static partial void LogSessionCreated(
        ILogger logger,
        Guid? repositorySessionId,
        string? repositoryProfile,
        int profilesQueried);

    [LoggerMessage(
        EventId = 1403,
        Level = LogLevel.Debug,
        Message =
            "Scheduled Jira selection completed with outcome {Outcome} after " +
            "querying {ProfilesQueried} enabled profiles and finding " +
            "{EligibleCandidateCount} eligible candidates.")]
    private static partial void LogPollResult(
        ILogger logger,
        JiraSelectionOutcome outcome,
        int profilesQueried,
        int eligibleCandidateCount);
}
