using AkosFabric.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace AkosFabric.Api.Health;

public sealed class RabbitMqReadinessHealthCheck : IHealthCheck
{
    private readonly RabbitMqOptions options;

    public RabbitMqReadinessHealthCheck(RabbitMqOptions options)
    {
        this.options = options
            ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var factory = new ConnectionFactory
            {
                Uri = options.ConnectionUri,
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                ClientProvidedName =
                    "akos-fabric-agent-control-readiness",
            };
            await using IConnection connection =
                await factory.CreateConnectionAsync(cancellationToken);
            await using IChannel channel =
                await connection.CreateChannelAsync(
                    cancellationToken: cancellationToken);
            return connection.IsOpen && channel.IsOpen
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    "RabbitMQ readiness connection was not open.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy(
                "RabbitMQ readiness connection failed.");
        }
    }
}
