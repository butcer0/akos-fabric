using System.Diagnostics;
using System.Diagnostics.Metrics;
using AkosFabric.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AkosFabric.IntegrationTests.Telemetry;

public sealed class AgentControlTelemetryTests
{
    [Fact]
    public void SpanNamesAreExactlyTheRequiredControlOperations()
    {
        Assert.Equal(
            [
                "jira.search",
                "jira.issue.fetch",
                "jira.transition",
                "repository_session.create",
                "rabbitmq.publish",
                "rabbitmq.consume",
                "docker.container.start",
                "docker.container.wait",
                "source_control.credential.acquire",
                "source_control.change_request.create",
                "source_control.review.publish",
                "repository_session.complete",
            ],
            AgentControlSpans.All);
    }

    [Fact]
    public void RemoteTraceParentAndCorrelationBaggageArePreserved()
    {
        using ActivityListener listener = ListenToControlActivities();
        const string traceParent =
            "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01";
        Guid sessionId = Guid.NewGuid();
        Guid workItemId = Guid.NewGuid();
        ActivityContext parent =
            AgentControlTelemetry.ParseTraceParent(traceParent);

        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.RabbitMqConsume,
            ActivityKind.Consumer,
            parent,
            new ControlCorrelation(sessionId, workItemId));

        Assert.NotNull(activity);
        Assert.Equal(parent.TraceId, activity.TraceId);
        Assert.Equal(parent.SpanId, activity.ParentSpanId);
        Assert.True(activity.HasRemoteParent);
        Assert.Equal(
            sessionId.ToString("D"),
            activity.GetBaggageItem(
                AgentControlTelemetry.RepositorySessionBaggageKey));
        Assert.Equal(
            workItemId.ToString("D"),
            activity.GetBaggageItem(
                AgentControlTelemetry.WorkItemBaggageKey));
        Assert.Equal(
            new ControlCorrelation(sessionId, workItemId),
            AgentControlTelemetry.ReadCorrelation(activity));
        Assert.Empty(activity.TagObjects);
    }

    [Fact]
    public void MetadataTagApiAllowsOnlySpecifiedBoundedMetadata()
    {
        using ActivityListener listener = ListenToControlActivities();
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.DockerContainerStart);
        Assert.NotNull(activity);

        MetadataOnlyTagPolicy.SetTag(
            activity,
            MetadataTag.ToolName,
            "docker");
        MetadataOnlyTagPolicy.SetTag(
            activity,
            MetadataTag.Success,
            true);
        MetadataOnlyTagPolicy.SetTag(
            activity,
            MetadataTag.StandardOutputByteCount,
            2048L);
        MetadataOnlyTagPolicy.SetTag(
            activity,
            MetadataTag.SourceControlProvider,
            "github");

        Assert.Equal("docker", activity.GetTagItem("tool.name"));
        Assert.Equal(true, activity.GetTagItem("operation.success"));
        Assert.Equal(2048L, activity.GetTagItem("output.stdout.bytes"));
        Assert.Equal(
            "github",
            activity.GetTagItem("source_control.provider"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MetadataOnlyTagPolicy.SetTag(
                activity,
                MetadataTag.StandardOutputByteCount,
                (long)int.MaxValue + 1));
        Assert.Throws<ArgumentException>(
            () => MetadataOnlyTagPolicy.SetTag(
                activity,
                MetadataTag.ToolName,
                "unsafe\nvalue"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MetadataOnlyTagPolicy.GetName(
                (MetadataTag)int.MaxValue));
    }

    [Theory]
    [InlineData("source.code")]
    [InlineData("agent.prompt.content")]
    [InlineData("gen_ai.model.response")]
    [InlineData("jira.description")]
    [InlineData("http.request.header.authorization")]
    [InlineData("db.query.text")]
    [InlineData("url.full")]
    [InlineData("source_control.credential")]
    [InlineData("exception.stacktrace")]
    public void SecretBearingOrContentBearingTagsAreNotExportSafe(
        string tagName)
    {
        Assert.False(MetadataOnlyTagPolicy.IsExportSafe(tagName));
    }

    [Fact]
    public void SafetyProcessorRemovesContentAndSecretTags()
    {
        using ActivityListener listener = ListenToControlActivities();
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.JiraIssueFetch);
        Assert.NotNull(activity);
        activity.SetTag("jira.description", "sensitive issue body");
        activity.SetTag("url.full", "https://jira.test/?token=secret");
        activity.SetTag("http.request.method", "GET");

        var processor = new MetadataOnlyActivityProcessor();
        processor.OnEnd(activity);

        Assert.Null(activity.GetTagItem("jira.description"));
        Assert.Null(activity.GetTagItem("url.full"));
        Assert.Equal(
            "GET",
            activity.GetTagItem("http.request.method"));
    }

    [Fact]
    public void InitialMetricsHaveExactNamesAndNoLabels()
    {
        var measurements =
            new List<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                    AgentControlTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
                measurements.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) =>
                measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();

        using var metrics = new AgentControlMetrics();
        metrics.RecordRepositorySession();
        metrics.RecordRepositorySessionDuration(TimeSpan.FromSeconds(2));
        metrics.RecordWorkItem();
        metrics.RecordWorkItemDuration(TimeSpan.FromSeconds(3));
        metrics.RecordModelRequest();
        metrics.RecordModelInputTokens(10);
        metrics.RecordModelOutputTokens(20);
        metrics.RecordModelCostUsd(0.25);
        metrics.RecordVerificationFailure();
        metrics.RecordJudgeDisposition();
        metrics.RecordChangeRequestCreated();

        Assert.Equal(
            AgentControlMetricNames.All.Order(StringComparer.Ordinal),
            measurements
                .Select(measurement => measurement.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(
            measurements,
            measurement => Assert.Empty(measurement.Tags));
    }

    [Fact]
    public void LifecycleMetricsUseOnlyBoundedMetadataLabels()
    {
        var measurements =
            new List<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                    AgentControlTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
                measurements.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) =>
                measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();

        using var metrics = new AgentControlMetrics();
        metrics.RecordRepositorySessionCreated("github");
        metrics.RecordRepositorySessionDuration(
            "github",
            "completed",
            TimeSpan.FromSeconds(2));
        metrics.RecordWorkItem("github", "branch_pushed");
        metrics.RecordModelUsage(
            "google",
            "gemini-3.6-flash",
            "planner",
            1,
            10,
            5,
            0.25m);
        metrics.RecordVerificationFailure("github");
        metrics.RecordJudgeDisposition("github", "accept");
        metrics.RecordChangeRequestCreated("github");

        Assert.All(
            measurements.SelectMany(measurement => measurement.Tags),
            tag => Assert.True(
                MetadataOnlyTagPolicy.IsMetricLabelAllowed(tag.Key),
                $"Metric label '{tag.Key}' is forbidden."));
        Assert.Contains(
            measurements,
            measurement =>
                measurement.Name ==
                    AgentControlMetricNames.ModelRequestsTotal
                && measurement.Tags.Any(
                    tag => tag.Key == "agent.role"
                           && Equals(tag.Value, "planner")));
        Assert.DoesNotContain(
            measurements.SelectMany(measurement => measurement.Tags),
            tag => tag.Key.Contains(
                ".id",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LifecycleLoggerEmitsOnlyRequiredSanitizedMetadata()
    {
        var capture = new CaptureLogger<AgentControlLifecycleLogger>();
        var lifecycleLogger = new AgentControlLifecycleLogger(capture);
        Guid sessionId = Guid.NewGuid();
        Guid workItemId = Guid.NewGuid();
        using var activity = new Activity("test").Start();

        lifecycleLogger.Log(
            sessionId,
            workItemId,
            "work_item_result_recorded",
            "github\nunsafe",
            "credential=value");

        string message = Assert.Single(capture.Messages);
        Assert.Contains(
            activity.TraceId.ToHexString(),
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            sessionId.ToString(),
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            workItemId.ToString(),
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "work_item_result_recorded",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "invalid_metadata",
            message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "github\nunsafe",
            message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "credential=value",
            message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("repository_session.id")]
    [InlineData("akos.repository_session.id")]
    [InlineData("work_item.id")]
    [InlineData("akos.work-item-id")]
    [InlineData("jira.key")]
    [InlineData("commit.sha")]
    [InlineData("change_request.number")]
    public void HighCardinalityIdentityIsForbiddenAsMetricLabel(
        string labelName)
    {
        Assert.False(
            MetadataOnlyTagPolicy.IsMetricLabelAllowed(labelName));
    }

    [Fact]
    public void DependencyInjectionExtensionRegistersTelemetrySafely()
    {
        const string exporterSecret = "Authorization=Bearer%20secret";
        var options = new AkosControlTelemetryOptions
        {
            OtlpEndpoint = new Uri("http://127.0.0.1:4317"),
            Protocol = AkosOtlpProtocol.Grpc,
            OtlpHeaders = exporterSecret,
        };
        var services = new ServiceCollection();

        IServiceCollection result =
            services.AddAkosControlTelemetry(options);

        Assert.Same(services, result);
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(AgentControlMetrics));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType.FullName?.Contains(
                    "OpenTelemetry",
                    StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            exporterSecret,
            options.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsRejectCredentialBearingEndpoint()
    {
        var options = new AkosControlTelemetryOptions
        {
            OtlpEndpoint =
                new Uri("https://user:secret@otel.example.test/"),
        };

        ArgumentException exception =
            Assert.Throws<ArgumentException>(options.Validate);

        Assert.DoesNotContain(
            "secret",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredProvidersConstructWithoutNetworkAccess()
    {
        var services = new ServiceCollection();
        services.AddAkosControlTelemetry(
            new AkosControlTelemetryOptions
            {
                OtlpEndpoint = new Uri("http://127.0.0.1:4317"),
            });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<TracerProvider>());
        Assert.NotNull(provider.GetRequiredService<MeterProvider>());
        Assert.NotNull(provider.GetRequiredService<AgentControlMetrics>());
    }

    [Fact]
    public void OtlpExporterReceivesEndpointProtocolAndSecretHeaders()
    {
        const string headers = "Authorization=Bearer%20deployment-secret";
        var configured = new OtlpExporterOptions();
        var options = new AkosControlTelemetryOptions
        {
            OtlpEndpoint =
                new Uri("https://collector.example.test/v1/traces"),
            Protocol = AkosOtlpProtocol.HttpProtobuf,
            OtlpHeaders = headers,
        };

        TelemetryServiceCollectionExtensions.ConfigureExporter(
            configured,
            options);

        Assert.Equal(options.OtlpEndpoint, configured.Endpoint);
        Assert.Equal(
            OtlpExportProtocol.HttpProtobuf,
            configured.Protocol);
        Assert.Equal(headers, configured.Headers);
    }

    [Fact]
    public void UnknownSpanNameCannotEscapeRequiredContract()
    {
        Assert.Throws<ArgumentException>(
            () => AgentControlTelemetry.StartActivity(
                "unlisted.operation"));
    }

    private static ActivityListener ListenToControlActivities()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == AgentControlTelemetry.ActivitySourceName,
            Sample = static (
                    ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (
                    ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
