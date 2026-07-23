namespace AkosFabric.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string DefaultExchange = "agent.work";
    public const string DefaultQueue = "agent.repository-session";
    public const string DefaultRoutingKey = "repository-session";

    public required Uri ConnectionUri { get; init; }

    public string Exchange { get; init; } = DefaultExchange;

    public string Queue { get; init; } = DefaultQueue;

    public string RoutingKey { get; init; } = DefaultRoutingKey;

    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (ConnectionUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new ArgumentException(
                "RabbitMQ connection URI must use amqp or amqps.",
                nameof(ConnectionUri));
        }

        if (string.IsNullOrWhiteSpace(Exchange)
            || string.IsNullOrWhiteSpace(Queue)
            || string.IsNullOrWhiteSpace(RoutingKey))
        {
            throw new ArgumentException(
                "RabbitMQ exchange, queue, and routing key must be non-empty.");
        }

        if (ConfirmationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConfirmationTimeout),
                "RabbitMQ confirmation timeout must be positive.");
        }
    }
}
