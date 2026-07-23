using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;

using Json.Schema;

using YamlDotNet.Serialization;

namespace AkosFabric.Infrastructure.RepositoryProfiles;

public sealed partial class FileRepositoryProfileProvider : IRepositoryProfileProvider
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

    private readonly string _profileRoot;
    private readonly JsonSchema _schema;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _jsonSerializer;

    public FileRepositoryProfileProvider(
        RepositoryProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            throw new RepositoryProfileException(
                "RepositoryProfiles:RootPath is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SchemaPath))
        {
            throw new RepositoryProfileException(
                "RepositoryProfiles:SchemaPath is required.");
        }

        _profileRoot = Path.GetFullPath(options.RootPath);
        if (!Directory.Exists(_profileRoot))
        {
            throw new RepositoryProfileException(
                $"Repository profile root does not exist: {_profileRoot}");
        }

        var schemaPath = Path.GetFullPath(options.SchemaPath);
        if (!File.Exists(schemaPath))
        {
            throw new RepositoryProfileException(
                $"Repository profile schema does not exist: {schemaPath}");
        }

        try
        {
            _schema = Schemas.GetOrAdd(
                schemaPath,
                static path => new Lazy<JsonSchema>(
                    () => JsonSchema.FromFile(
                        path,
                        new BuildOptions
                        {
                            Dialect = Dialect.Draft202012,
                            SchemaRegistry = new SchemaRegistry(),
                        }),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        catch (Exception exception)
        {
            throw new RepositoryProfileException(
                $"Cannot load repository profile schema: {schemaPath}",
                exception);
        }

        _yamlDeserializer = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        _jsonSerializer = new SerializerBuilder().JsonCompatible().Build();
    }

    public async Task<RepositoryProfile?> FindAsync(
        string profileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileName) ||
            !ProfileNamePattern().IsMatch(profileName))
        {
            throw new RepositoryProfileException(
                "Repository profile names must match ^[a-z][a-z0-9-]*$.");
        }

        var profilesDirectory = Path.Combine(_profileRoot, "profiles");
        var profilePath = Path.GetFullPath(
            Path.Combine(profilesDirectory, $"{profileName}.yml"));
        if (!profilePath.StartsWith(
                Path.GetFullPath(profilesDirectory) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new RepositoryProfileException(
                "Repository profile path escapes the configured checkout.");
        }

        if (!File.Exists(profilePath))
        {
            return null;
        }

        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(profilePath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RepositoryProfileException(
                $"Cannot read repository profile '{profileName}'.",
                exception);
        }

        JsonDocument document;
        try
        {
            using var yamlReader = new StringReader(yaml);
            var yamlObject = _yamlDeserializer.Deserialize<object>(yamlReader);
            var json = _jsonSerializer.Serialize(yamlObject);
            document = JsonDocument.Parse(json);
        }
        catch (Exception exception)
        {
            throw new RepositoryProfileException(
                $"Repository profile '{profileName}' is not valid YAML.",
                exception);
        }

        using (document)
        {
            var evaluation = _schema.Evaluate(
                document.RootElement,
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.List,
                    RequireFormatValidation = true,
                });
            if (!evaluation.IsValid)
            {
                var failures = FormatValidationFailures(evaluation);
                throw new RepositoryProfileException(
                    $"Repository profile '{profileName}' does not conform to schema v1: {failures}");
            }

            var revision = await GitProfileRevisionResolver.ResolveAsync(
                _profileRoot,
                cancellationToken);
            var profileJson = JsonNode.Parse(document.RootElement.GetRawText())
                as JsonObject
                ?? throw new RepositoryProfileException(
                    $"Repository profile '{profileName}' must be a YAML object.");
            profileJson["profileRevisionSha"] = revision;

            RepositoryProfile? profile;
            try
            {
                profile = profileJson.Deserialize<RepositoryProfile>(
                    SerializerOptions);
            }
            catch (Exception exception)
            {
                throw new RepositoryProfileException(
                    $"Repository profile '{profileName}' cannot be materialized.",
                    exception);
            }

            if (profile is null)
            {
                throw new RepositoryProfileException(
                    $"Repository profile '{profileName}' is empty.");
            }

            if (!string.Equals(profile.Id, profileName, StringComparison.Ordinal))
            {
                throw new RepositoryProfileException(
                    $"Repository profile ID '{profile.Id}' does not match requested name '{profileName}'.");
            }

            return profile;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileNamePattern();

    private static string FormatValidationFailures(
        EvaluationResults evaluation)
    {
        var failures = EnumerateFailures(evaluation)
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
                    $"instance {evaluation.InstanceLocation}, schema {evaluation.EvaluationPath}: {error.Key}: {error.Value}";
            }
        }

        if (evaluation.Details is null)
        {
            yield break;
        }

        foreach (var detail in evaluation.Details)
        {
            foreach (var failure in EnumerateFailures(detail))
            {
                yield return failure;
            }
        }
    }
}
