using System.Diagnostics;
using System.Text;

using AkosFabric.Application.Messaging;
using AkosFabric.Infrastructure.Telemetry;

using Microsoft.Extensions.Hosting;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AkosFabric.Infrastructure.Messaging;

public sealed class RabbitMqRepositorySessionConsumer : BackgroundService
{
    private readonly RabbitMqOptions options;
    private readonly RepositorySessionDeliveryHandler handler;

    public RabbitMqRepositorySessionConsumer(
        RabbitMqOptions options,
        RepositorySessionDeliveryHandler handler)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = options.ConnectionUri,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1,
            ClientProvidedName = "akos-fabric-agent-control-consumer",
        };

        await using IConnection connection =
            await factory.CreateConnectionAsync(stoppingToken);
        await using IChannel channel =
            await connection.CreateChannelAsync(
                cancellationToken: stoppingToken);

        await RabbitMqTopology.DeclareAsync(
            channel,
            options,
            stoppingToken);
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 1,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                ValidateEnvelope(delivery);
                RepositorySessionRequestedV1 message =
                    RepositorySessionMessageCodec.Deserialize(delivery.Body);
                ValidateMessageProperties(delivery.BasicProperties, message);
                using Activity? activity = AgentControlTelemetry.StartActivity(
                    AgentControlSpans.RabbitMqConsume,
                    ActivityKind.Consumer,
                    AgentControlTelemetry.ParseTraceParent(
                        message.TraceParent),
                    new ControlCorrelation(
                        message.RepositorySessionId));

                RepositorySessionDeliveryResult result =
                    await handler.HandleAsync(message, stoppingToken);
                if (result.Disposition ==
                    RepositorySessionDeliveryDisposition.Acknowledge)
                {
                    await channel.BasicAckAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        stoppingToken);
                }
                else
                {
                    await channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Closing the channel leaves an unacknowledged delivery
                // eligible for broker redelivery after restart.
            }
            catch (RepositorySessionMessageFormatException)
            {
                await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: false,
                    stoppingToken);
            }
            catch (Exception)
            {
                await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    requeue: true,
                    stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(
            options.Queue,
            autoAck: false,
            consumerTag: "akos-fabric-agent-control-v1",
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer,
            stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void ValidateEnvelope(BasicDeliverEventArgs delivery)
    {
        if (!string.Equals(
                delivery.Exchange,
                options.Exchange,
                StringComparison.Ordinal) ||
            !string.Equals(
                delivery.RoutingKey,
                options.RoutingKey,
                StringComparison.Ordinal))
        {
            throw new RepositorySessionMessageFormatException(
                "Repository-session delivery used unexpected routing metadata.");
        }
    }

    private static void ValidateMessageProperties(
        IReadOnlyBasicProperties properties,
        RepositorySessionRequestedV1 message)
    {
        if (!string.Equals(
                properties.ContentType,
                RepositorySessionMessageCodec.ContentType,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                properties.ContentEncoding,
                RepositorySessionMessageCodec.ContentEncoding,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                properties.Type,
                RepositorySessionMessageCodec.MessageType,
                StringComparison.Ordinal) ||
            properties.DeliveryMode != DeliveryModes.Persistent ||
            !Guid.TryParseExact(properties.MessageId, "D", out Guid messageId) ||
            messageId != message.MessageId ||
            properties.Timestamp.UnixTime !=
                message.RequestedAt.ToUnixTimeSeconds() ||
            !TryReadTraceParent(properties.Headers, out string traceParent) ||
            !string.Equals(
                traceParent,
                message.TraceParent,
                StringComparison.Ordinal))
        {
            throw new RepositorySessionMessageFormatException(
                "Repository-session AMQP properties do not match the schema-v1 message.");
        }
    }

    private static bool TryReadTraceParent(
        IDictionary<string, object?>? headers,
        out string traceParent)
    {
        traceParent = string.Empty;
        if (headers is null ||
            !headers.TryGetValue("traceparent", out object? value))
        {
            return false;
        }

        traceParent = value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span),
            string text => text,
            _ => string.Empty,
        };
        return traceParent.Length > 0;
    }
}
