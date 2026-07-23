using AkosFabric.Api.Health;
using AkosFabric.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AkosFabric.IntegrationTests.Hosting;

public sealed class ReadinessHealthCheckTests
{
    [Fact]
    [Trait("Dependency", "PostgreSQL")]
    public async Task PostgreSqlProbeExecutesNonMutatingReadinessQuery()
    {
        string connectionString =
            Environment.GetEnvironmentVariable(
                "AKOS_POSTGRES_CONNECTION_STRING")
            ?? "Host=127.0.0.1;Port=5432;Database=akos_fabric;" +
            "Username=akos_fabric;" +
            "Password=akos-local-postgres-7P3n6Nf4wR9k;" +
            "Timeout=5;Command Timeout=15";
        await using NpgsqlDataSource dataSource =
            NpgsqlDataSource.Create(connectionString);
        var check = new PostgresReadinessHealthCheck(dataSource);

        HealthCheckResult result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Empty(result.Data);
    }

    [Fact]
    [Trait("Dependency", "RabbitMQ")]
    public async Task RabbitMqProbeOpensConnectionWithoutDeclaringTopology()
    {
        string? connectionUri = Environment.GetEnvironmentVariable(
            "AKOS_RABBITMQ_TEST_URI");
        if (string.IsNullOrWhiteSpace(connectionUri))
        {
            return;
        }

        var check = new RabbitMqReadinessHealthCheck(
            new RabbitMqOptions
            {
                ConnectionUri = new Uri(connectionUri),
            });

        HealthCheckResult result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Empty(result.Data);
    }
}
