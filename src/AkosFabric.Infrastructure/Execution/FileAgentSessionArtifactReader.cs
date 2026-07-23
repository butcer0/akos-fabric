using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;

using Json.Schema;

namespace AkosFabric.Infrastructure.Execution;

public sealed class FileAgentSessionArtifactReader
    : IAgentSessionArtifactReader
{
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>>
        Schemas = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly SessionFileStore fileStore;
    private readonly JsonSchema manifestSchema;
    private readonly JsonSchema resultSchema;

    public FileAgentSessionArtifactReader(
        SessionFileStore fileStore,
        AgentSessionArtifactReaderOptions options)
    {
        this.fileStore = fileStore
            ?? throw new ArgumentNullException(nameof(fileStore));
        ArgumentNullException.ThrowIfNull(options);
        manifestSchema = LoadSchema(
            options.ManifestSchemaPath,
            "manifest");
        resultSchema = LoadSchema(
            options.ResultSchemaPath,
            "result");
    }

    public async Task<AgentSessionArtifactsV1> ReadAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        string manifestJson = await ReadRequiredAsync(
            fileStore.GetSessionFile(
                repositorySessionId,
                SessionFileStore.ManifestFileName),
            "manifest.json",
            cancellationToken);
        string resultJson = await ReadRequiredAsync(
            fileStore.GetSessionFile(
                repositorySessionId,
                SessionFileStore.ResultFileName),
            "result.json",
            cancellationToken);

        AgentSessionManifestV1 manifest = ValidateAndDeserialize<
            AgentSessionManifestV1>(
            manifestJson,
            manifestSchema,
            "manifest.json");
        AgentSessionResultV1 result = ValidateAndDeserialize<
            AgentSessionResultV1>(
            resultJson,
            resultSchema,
            "result.json");

        using var document = JsonDocument.Parse(resultJson);
        var itemJson = new Dictionary<Guid, string>();
        foreach (JsonElement item in
                 document.RootElement.GetProperty("items").EnumerateArray())
        {
            Guid itemId = item.GetProperty("workItemRunId").GetGuid();
            if (!itemJson.TryAdd(itemId, item.GetRawText()))
            {
                throw new AgentResultValidationException(
                    $"result.json contains duplicate work-item ID '{itemId}'.");
            }
        }

        return new AgentSessionArtifactsV1(
            manifest,
            result,
            resultJson,
            itemJson);
    }

    private static JsonSchema LoadSchema(
        string configuredPath,
        string description)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException(
                $"The {description} schema path is required.",
                nameof(configuredPath));
        }

        string path = Path.GetFullPath(configuredPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The {description} schema does not exist.",
                path);
        }

        try
        {
            return Schemas.GetOrAdd(
                path,
                static schemaPath => new Lazy<JsonSchema>(
                    () => JsonSchema.FromFile(
                        schemaPath,
                        new BuildOptions
                        {
                            Dialect = Dialect.Draft202012,
                            SchemaRegistry = new SchemaRegistry(),
                        }),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        catch (Exception exception)
        {
            throw new AgentResultValidationException(
                $"Cannot load {description} schema '{path}'.",
                exception);
        }
    }

    private static async Task<string> ReadRequiredAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            throw new AgentResultValidationException(
                $"Cannot read completed {description} at '{path}'.",
                exception);
        }
    }

    private static T ValidateAndDeserialize<T>(
        string json,
        JsonSchema schema,
        string description)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new AgentResultValidationException(
                $"{description} is not valid JSON.",
                exception);
        }

        using (document)
        {
            EvaluationResults evaluation = schema.Evaluate(
                document.RootElement,
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.List,
                    RequireFormatValidation = true,
                });
            if (!evaluation.IsValid)
            {
                throw new AgentResultValidationException(
                    $"{description} does not conform to schema v1: " +
                    FormatValidationFailures(evaluation));
            }

            try
            {
                return JsonSerializer.Deserialize<T>(
                           document.RootElement,
                           SerializerOptions)
                       ?? throw new AgentResultValidationException(
                           $"{description} is empty.");
            }
            catch (JsonException exception)
            {
                throw new AgentResultValidationException(
                    $"{description} cannot be materialized.",
                    exception);
            }
        }
    }

    private static string FormatValidationFailures(
        EvaluationResults evaluation)
    {
        string[] failures = EnumerateFailures(evaluation)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return failures.Length == 0
            ? $"instance {evaluation.InstanceLocation}"
            : string.Join("; ", failures);
    }

    private static IEnumerable<string> EnumerateFailures(
        EvaluationResults evaluation)
    {
        if (!evaluation.IsValid && evaluation.Errors is not null)
        {
            foreach (var error in evaluation.Errors)
            {
                yield return
                    $"instance {evaluation.InstanceLocation}, " +
                    $"schema {evaluation.EvaluationPath}: " +
                    $"{error.Key}: {error.Value}";
            }
        }

        if (evaluation.Details is null)
        {
            yield break;
        }

        foreach (EvaluationResults detail in evaluation.Details)
        {
            foreach (string failure in EnumerateFailures(detail))
            {
                yield return failure;
            }
        }
    }
}
