using System.Diagnostics;
using System.Diagnostics.Metrics;
using AkosFabric.Application.Telemetry;

namespace AkosFabric.Infrastructure.Telemetry;

public sealed class AgentControlMetrics : IAgentControlMetrics, IDisposable
{
    private readonly Meter meter =
        new(
            AgentControlTelemetry.MeterName,
            AgentControlTelemetry.InstrumentationVersion);
    private readonly Counter<long> repositorySessions;
    private readonly Histogram<double> repositorySessionDuration;
    private readonly Counter<long> workItems;
    private readonly Histogram<double> workItemDuration;
    private readonly Counter<long> modelRequests;
    private readonly Counter<long> modelInputTokens;
    private readonly Counter<long> modelOutputTokens;
    private readonly Counter<double> modelCost;
    private readonly Counter<long> verificationFailures;
    private readonly Counter<long> judgeDispositions;
    private readonly Counter<long> changeRequestsCreated;

    public AgentControlMetrics()
    {
        repositorySessions = meter.CreateCounter<long>(
            AgentControlMetricNames.RepositorySessionsTotal);
        repositorySessionDuration = meter.CreateHistogram<double>(
            AgentControlMetricNames.RepositorySessionDurationSeconds,
            unit: "s");
        workItems = meter.CreateCounter<long>(
            AgentControlMetricNames.WorkItemsTotal);
        workItemDuration = meter.CreateHistogram<double>(
            AgentControlMetricNames.WorkItemDurationSeconds,
            unit: "s");
        modelRequests = meter.CreateCounter<long>(
            AgentControlMetricNames.ModelRequestsTotal);
        modelInputTokens = meter.CreateCounter<long>(
            AgentControlMetricNames.ModelInputTokensTotal);
        modelOutputTokens = meter.CreateCounter<long>(
            AgentControlMetricNames.ModelOutputTokensTotal);
        modelCost = meter.CreateCounter<double>(
            AgentControlMetricNames.ModelCostUsdTotal,
            unit: "USD");
        verificationFailures = meter.CreateCounter<long>(
            AgentControlMetricNames.VerificationFailuresTotal);
        judgeDispositions = meter.CreateCounter<long>(
            AgentControlMetricNames.JudgeDispositionsTotal);
        changeRequestsCreated = meter.CreateCounter<long>(
            AgentControlMetricNames.ChangeRequestsCreatedTotal);
    }

    public void RecordRepositorySession() => repositorySessions.Add(1);

    public void RecordRepositorySessionDuration(TimeSpan duration) =>
        repositorySessionDuration.Record(ValidateDuration(duration));

    public void RecordWorkItem() => workItems.Add(1);

    public void RecordWorkItemDuration(TimeSpan duration) =>
        workItemDuration.Record(ValidateDuration(duration));

    public void RecordModelRequest() => modelRequests.Add(1);

    public void RecordModelInputTokens(long count) =>
        modelInputTokens.Add(ValidateCount(count));

    public void RecordModelOutputTokens(long count) =>
        modelOutputTokens.Add(ValidateCount(count));

    public void RecordModelCostUsd(double cost)
    {
        if (!double.IsFinite(cost) || cost < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cost),
                "Model cost must be a finite non-negative value.");
        }

        modelCost.Add(cost);
    }

    public void RecordVerificationFailure() =>
        verificationFailures.Add(1);

    public void RecordJudgeDisposition() => judgeDispositions.Add(1);

    public void RecordChangeRequestCreated() =>
        changeRequestsCreated.Add(1);

    public void RecordRepositorySessionCreated(
        string sourceControlProvider) =>
        repositorySessions.Add(
            1,
            ProviderTag(sourceControlProvider));

    public void RecordRepositorySessionDuration(
        string sourceControlProvider,
        string outcome,
        TimeSpan duration) =>
        repositorySessionDuration.Record(
            ValidateDuration(duration),
            ProviderTag(sourceControlProvider),
            OutcomeTag(outcome));

    public void RecordWorkItem(
        string sourceControlProvider,
        string outcome) =>
        workItems.Add(
            1,
            ProviderTag(sourceControlProvider),
            OutcomeTag(outcome));

    public void RecordModelUsage(
        string modelProvider,
        string model,
        string role,
        long requestCount,
        long inputTokens,
        long outputTokens,
        decimal estimatedCostUsd)
    {
        TagList tags =
        [
            new("gen_ai.provider.name", ValidateLabel(
                modelProvider,
                nameof(modelProvider))),
            new("gen_ai.request.model", ValidateLabel(
                model,
                nameof(model))),
            new("agent.role", ValidateLabel(role, nameof(role))),
        ];
        modelRequests.Add(ValidateCount(requestCount), tags);
        modelInputTokens.Add(ValidateCount(inputTokens), tags);
        modelOutputTokens.Add(ValidateCount(outputTokens), tags);
        RecordModelCostUsd(
            decimal.ToDouble(estimatedCostUsd),
            tags);
    }

    public void RecordVerificationFailure(
        string sourceControlProvider) =>
        verificationFailures.Add(
            1,
            ProviderTag(sourceControlProvider));

    public void RecordJudgeDisposition(
        string sourceControlProvider,
        string disposition) =>
        judgeDispositions.Add(
            1,
            ProviderTag(sourceControlProvider),
            new KeyValuePair<string, object?>(
                "judge.disposition",
                ValidateLabel(disposition, nameof(disposition))));

    public void RecordChangeRequestCreated(
        string sourceControlProvider) =>
        changeRequestsCreated.Add(
            1,
            ProviderTag(sourceControlProvider));

    public void Dispose() => meter.Dispose();

    private void RecordModelCostUsd(
        double cost,
        in TagList tags)
    {
        if (!double.IsFinite(cost) || cost < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cost),
                "Model cost must be a finite non-negative value.");
        }

        modelCost.Add(cost, tags);
    }

    private static KeyValuePair<string, object?> ProviderTag(
        string provider) =>
        new(
            "source_control.provider",
            ValidateLabel(provider, nameof(provider)));

    private static KeyValuePair<string, object?> OutcomeTag(
        string outcome) =>
        new(
            "outcome",
            ValidateLabel(outcome, nameof(outcome)));

    private static string ValidateLabel(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Metric labels must be non-empty bounded metadata.",
                parameterName);
        }

        return value;
    }

    private static double ValidateDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Telemetry duration cannot be negative.");
        }

        return duration.TotalSeconds;
    }

    private static long ValidateCount(long count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "Telemetry count cannot be negative.");
        }

        return count;
    }
}
