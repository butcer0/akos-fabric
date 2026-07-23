using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AkosFabric.Infrastructure.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddAkosControlTelemetry(
        this IServiceCollection services,
        AkosControlTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<AgentControlMetrics>();
        services.AddLogging(logging => logging.AddOpenTelemetry(
            loggingOptions =>
            {
                loggingOptions.IncludeFormattedMessage = false;
                loggingOptions.IncludeScopes = false;
                loggingOptions.ParseStateValues = true;
                loggingOptions.SetResourceBuilder(
                    ResourceBuilder.CreateDefault().AddService(
                        options.ServiceName,
                        serviceVersion: options.ServiceVersion));
                loggingOptions.AddOtlpExporter(
                    exporter => ConfigureExporter(exporter, options));
            }));

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                options.ServiceName,
                serviceVersion: options.ServiceVersion))
            .WithTracing(tracing => tracing
                .AddSource(AgentControlTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation(
                    instrumentation =>
                        instrumentation.RecordException = false)
                .AddHttpClientInstrumentation(
                    instrumentation =>
                        instrumentation.RecordException = false)
                .AddNpgsql()
                .AddProcessor(new MetadataOnlyActivityProcessor())
                .AddOtlpExporter(
                    exporter => ConfigureExporter(exporter, options)))
            .WithMetrics(metrics => metrics
                .AddMeter(AgentControlTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(
                    exporter => ConfigureExporter(exporter, options)));

        return services;
    }

    internal static void ConfigureExporter(
        OtlpExporterOptions exporter,
        AkosControlTelemetryOptions options)
    {
        exporter.Endpoint = options.OtlpEndpoint;
        exporter.Protocol = options.Protocol switch
        {
            AkosOtlpProtocol.Grpc => OtlpExportProtocol.Grpc,
            AkosOtlpProtocol.HttpProtobuf =>
                OtlpExportProtocol.HttpProtobuf,
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Protocol,
                "Unsupported OTLP protocol."),
        };
        if (options.OtlpHeaders is not null)
        {
            exporter.Headers = options.OtlpHeaders;
        }
    }
}
