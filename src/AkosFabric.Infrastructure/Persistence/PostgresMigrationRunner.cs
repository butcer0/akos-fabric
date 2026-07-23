using System.Security.Cryptography;
using System.Text;

using Npgsql;
using NpgsqlTypes;

namespace AkosFabric.Infrastructure.Persistence;

public sealed class PostgresMigrationRunner
{
    private const long MigrationLockId = 4_106_590_381_682_458_181;

    private static readonly string[] MigrationResources =
    [
        "AkosFabric.Infrastructure.Persistence.Migrations.001_InitialLedger.sql",
    ];

    private readonly NpgsqlDataSource dataSource;

    public PostgresMigrationRunner(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource
            ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS akos_fabric_schema_migration (
                version     text PRIMARY KEY,
                checksum    text NOT NULL,
                applied_at  timestamptz NOT NULL DEFAULT now()
            );
            """,
            cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock($1);",
                         connection,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue(
                NpgsqlDbType.Bigint,
                MigrationLockId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var resourceName in MigrationResources)
        {
            var version = GetVersion(resourceName);
            var migrationSql = ReadEmbeddedMigration(resourceName);
            var normalizedMigrationSql =
                migrationSql.ReplaceLineEndings("\n");
            var checksum = Convert.ToHexStringLower(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(normalizedMigrationSql)));

            var recordedChecksum = await FindChecksumAsync(
                connection,
                transaction,
                version,
                cancellationToken);

            if (recordedChecksum is not null)
            {
                if (!string.Equals(
                        recordedChecksum,
                        checksum,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Migration {version} has changed after it was applied.");
                }

                continue;
            }

            await ExecuteAsync(
                connection,
                transaction,
                migrationSql,
                cancellationToken);

            await using var recordCommand = new NpgsqlCommand(
                """
                INSERT INTO akos_fabric_schema_migration (version, checksum)
                VALUES ($1, $2);
                """,
                connection,
                transaction);
            recordCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                version);
            recordCommand.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                checksum);
            await recordCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> FindChecksumAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT checksum
            FROM akos_fabric_schema_migration
            WHERE version = $1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, version);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static string GetVersion(string resourceName)
    {
        const string suffix = ".sql";
        var start = resourceName.LastIndexOf(
            '.',
            resourceName.Length - suffix.Length - 1);
        return resourceName[
            (start + 1)..^suffix.Length];
    }

    private static string ReadEmbeddedMigration(string resourceName)
    {
        var assembly = typeof(PostgresMigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration {resourceName} was not found.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
