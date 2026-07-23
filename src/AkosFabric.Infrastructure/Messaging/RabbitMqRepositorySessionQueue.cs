using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Infrastructure.Telemetry;
using RabbitMQ.Client;

namespace AkosFabric.Infrastructure.Messaging;

[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The architecture specification names this transport capability a repository-session queue.")]
public sealed class RabbitMqRepositorySessionQueue : IRepositorySessionQueue
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly RabbitMqOptions _options;

    public RabbitMqRepositorySessionQueue(RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task PublishAsync(
        RepositorySessionRequestedV1 message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        RepositorySessionMessageCodec.Validate(message);
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.RabbitMqPublish,
            ActivityKind.Producer,
            AgentControlTelemetry.ParseTraceParent(message.TraceParent),
            new ControlCorrelation(message.RepositorySessionId));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.ConfirmationTimeout);

        var factory = new ConnectionFactory
        {
            Uri = _options.ConnectionUri,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ClientProvidedName = "akos-fabric-agent-control-publisher",
        };

        await using var connection = await factory.CreateConnectionAsync(
            timeout.Token);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(
            channelOptions,
            timeout.Token);

        await RabbitMqTopology.DeclareAsync(
            channel,
            _options,
            timeout.Token);

        var body = Serialize(message);
        var properties = new BasicProperties
        {
            ContentType = RepositorySessionMessageCodec.ContentType,
            ContentEncoding = RepositorySessionMessageCodec.ContentEncoding,
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.MessageId.ToString("D"),
            Type = RepositorySessionMessageCodec.MessageType,
            Timestamp = new AmqpTimestamp(message.RequestedAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                ["traceparent"] = message.TraceParent,
            },
        };

        await channel.BasicPublishAsync(
            _options.Exchange,
            _options.RoutingKey,
            mandatory: true,
            properties,
            body,
            timeout.Token);
    }

    internal static byte[] Serialize(RepositorySessionRequestedV1 message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
    }

}
