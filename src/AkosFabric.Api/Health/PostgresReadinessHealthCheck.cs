using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AkosFabric.Api.Health;

public sealed class PostgresReadinessHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresReadinessHealthCheck(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource
            ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using NpgsqlConnection connection =
                await dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlCommand command =
                connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            object? result =
                await command.ExecuteScalarAsync(cancellationToken);
            return result is int value && value == 1
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    "PostgreSQL readiness query returned an unexpected result.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy(
                "PostgreSQL readiness query failed.");
        }
    }
}
