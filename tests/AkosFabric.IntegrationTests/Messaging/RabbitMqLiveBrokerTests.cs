using System.Text.Json;

using AkosFabric.Application.Messaging;
using AkosFabric.Infrastructure.Messaging;

using RabbitMQ.Client;

namespace AkosFabric.IntegrationTests.Messaging;

public sealed class RabbitMqLiveBrokerTests
{
    [Fact]
    [Trait("Dependency", "RabbitMQ")]
    public async Task PublishesConfirmedPersistentMessageToExactClassicTopology()
    {
        var connectionUri = Environment.GetEnvironmentVariable(
            "AKOS_RABBITMQ_TEST_URI");
        if (string.IsNullOrWhiteSpace(connectionUri))
        {
            return;
        }

        var options = new RabbitMqOptions
        {
            ConnectionUri = new Uri(connectionUri),
            ConfirmationTimeout = TimeSpan.FromSeconds(10),
        };
        var factory = new ConnectionFactory
        {
            Uri = options.ConnectionUri,
        };
        await using var connection = await factory.CreateConnectionAsync(
            CancellationToken.None);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: CancellationToken.None);
        await channel.ExchangeDeclareAsync(
            options.Exchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: CancellationToken.None);
        await channel.QueueDeclareAsync(
            options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "classic",
            },
            cancellationToken: CancellationToken.None);
        await channel.QueueBindAsync(
            options.Queue,
            options.Exchange,
            options.RoutingKey,
            arguments: null,
            cancellationToken: CancellationToken.None);
        await channel.QueuePurgeAsync(
            options.Queue,
            CancellationToken.None);

        var message = new RepositorySessionRequestedV1(
            SchemaVersion: 1,
            MessageId: Guid.NewGuid(),
            RepositorySessionId: Guid.NewGuid(),
            TraceParent:
                "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01",
            RequestedAt: DateTimeOffset.UtcNow);
        var publisher = new RabbitMqRepositorySessionQueue(options);

        await publisher.PublishAsync(
            message,
            CancellationToken.None);

        var delivery = await channel.BasicGetAsync(
            options.Queue,
            autoAck: false,
            CancellationToken.None);
        Assert.NotNull(delivery);
        Assert.Equal(DeliveryModes.Persistent, delivery.BasicProperties.DeliveryMode);
        Assert.Equal(
            message.MessageId.ToString("D"),
            delivery.BasicProperties.MessageId);
        Assert.Equal("repository-session.v1", delivery.BasicProperties.Type);
        using var body = JsonDocument.Parse(delivery.Body);
        Assert.Equal(
            message.RepositorySessionId,
            body.RootElement.GetProperty("repositorySessionId").GetGuid());

        await channel.BasicAckAsync(
            delivery.DeliveryTag,
            multiple: false,
            CancellationToken.None);
    }
}
