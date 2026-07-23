using System.Diagnostics;

namespace AkosFabric.Infrastructure.Telemetry;

public static class AgentControlTelemetry
{
    public const string ActivitySourceName =
        "AkosFabric.AgentControl";
    public const string MeterName = "AkosFabric.AgentControl";
    public const string InstrumentationVersion = "1.4.0";
    public const string RepositorySessionBaggageKey =
        "akos.repository_session.id";
    public const string WorkItemBaggageKey = "akos.work_item.id";

    public static readonly ActivitySource ActivitySource =
        new(ActivitySourceName, InstrumentationVersion);

    public static Activity? StartActivity(
        string spanName,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default,
        ControlCorrelation correlation = default)
    {
        if (!AgentControlSpans.All.Contains(
                spanName,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The span name is not part of the required control-plane telemetry contract.",
                nameof(spanName));
        }

        correlation.Validate();
        Activity? activity = parentContext == default
            ? ActivitySource.StartActivity(spanName, kind)
            : ActivitySource.StartActivity(spanName, kind, parentContext);
        ApplyCorrelation(activity, correlation);
        return activity;
    }

    public static ActivityContext ParseTraceParent(string traceParent)
    {
        if (!ActivityContext.TryParse(
                traceParent,
                traceState: null,
                isRemote: true,
                out ActivityContext context))
        {
            throw new ArgumentException(
                "A valid W3C traceparent is required.",
                nameof(traceParent));
        }

        return context;
    }

    public static void ApplyCorrelation(
        Activity? activity,
        ControlCorrelation correlation)
    {
        correlation.Validate();
        if (activity is null || correlation.RepositorySessionId == Guid.Empty)
        {
            return;
        }

        activity.SetBaggage(
            RepositorySessionBaggageKey,
            correlation.RepositorySessionId.ToString("D"));
        if (correlation.WorkItemId is Guid workItemId)
        {
            activity.SetBaggage(
                WorkItemBaggageKey,
                workItemId.ToString("D"));
        }
    }

    public static ControlCorrelation ReadCorrelation(Activity? activity)
    {
        if (activity is null ||
            !Guid.TryParseExact(
                activity.GetBaggageItem(RepositorySessionBaggageKey),
                "D",
                out Guid repositorySessionId))
        {
            return default;
        }

        Guid? workItemId = Guid.TryParseExact(
            activity.GetBaggageItem(WorkItemBaggageKey),
            "D",
            out Guid parsedWorkItemId)
                ? parsedWorkItemId
                : null;
        return new ControlCorrelation(repositorySessionId, workItemId);
    }
}

public readonly record struct ControlCorrelation(
    Guid RepositorySessionId,
    Guid? WorkItemId = null)
{
    internal void Validate()
    {
        if (RepositorySessionId == Guid.Empty && WorkItemId is not null)
        {
            throw new ArgumentException(
                "A work-item correlation requires a repository-session correlation.");
        }

        if (WorkItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A work-item correlation ID cannot be empty.");
        }
    }
}
