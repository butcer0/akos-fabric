using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AkosFabric.Infrastructure.Execution;

public sealed partial class DockerContainerClient
{
    internal const string SystemLabelName = "agent.system";
    internal const string SystemLabelValue = "autonomous-engineering";
    internal const string SessionLabelName = "agent.repository-session-id";
    internal const string ProfileLabelName = "agent.repository-profile";

    private const string InspectFormat =
        "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"state\":{{json .State.Status}}," +
        "\"exitCode\":{{json .State.ExitCode}},\"startedAt\":{{json .State.StartedAt}}," +
        "\"finishedAt\":{{json .State.FinishedAt}},\"labels\":{{json .Config.Labels}}," +
        "\"image\":{{json .Config.Image}},\"user\":{{json .Config.User}}," +
        "\"init\":{{json .HostConfig.Init}},\"nanoCpus\":{{json .HostConfig.NanoCpus}}," +
        "\"memory\":{{json .HostConfig.Memory}},\"pidsLimit\":{{json .HostConfig.PidsLimit}}," +
        "\"stopTimeout\":{{json .Config.StopTimeout}},\"mounts\":{{json .Mounts}}}";

    private readonly DockerExecutionOptions options;
    private readonly IProcessRunner processRunner;

    public DockerContainerClient(DockerExecutionOptions options)
        : this(options, new ProcessRunner())
    {
    }

    internal DockerContainerClient(
        DockerExecutionOptions options,
        IProcessRunner processRunner)
    {
        this.options = ValidateOptions(options);
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    internal async Task<string> RunAsync(
        DockerRunRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRunRequest(request);
        var image = $"{request.ImageReference}@{request.ImageDigest}";

        var arguments = new List<string>
        {
            "run",
            "--detach",
            "--name",
            request.ContainerName,
            "--label",
            $"{SystemLabelName}={SystemLabelValue}",
            "--label",
            $"{SessionLabelName}={request.RepositorySessionId:D}",
            "--label",
            $"{ProfileLabelName}={request.RepositoryProfile}",
            "--init",
            "--user",
            $"{options.ContainerUserId}:{options.ContainerGroupId}",
            "--cpus",
            options.CpuLimit.ToString(CultureInfo.InvariantCulture),
            "--memory",
            options.MemoryLimit,
            "--pids-limit",
            options.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--stop-timeout",
            options.StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--mount",
            $"type=bind,src={request.SessionDirectory},dst=/run/agent",
        };

        AddEnvironment(arguments, "AGENT_SESSION_ID", request.RepositorySessionId.ToString("D"));
        AddEnvironment(arguments, "TASK_MANIFEST", "/run/agent/manifest.json");
        AddEnvironment(arguments, "RESULT_PATH", "/run/agent/result.json");
        AddEnvironment(
            arguments,
            "SOURCE_CONTROL_CREDENTIAL_PATH",
            "/run/agent/source-control-credential.json");
        AddEnvironment(arguments, "OTEL_EXPORTER_OTLP_ENDPOINT", request.OpenTelemetryEndpoint.ToString());
        AddEnvironment(arguments, "TRACEPARENT", request.TraceParent ?? string.Empty);
        AddEnvironment(arguments, "LLM_PROVIDER", request.LlmProvider);
        AddEnvironment(arguments, "LLM_MODEL", request.LlmModel);

        // The value is inherited by the Docker CLI from its private process
        // environment. It is not present in argv, the manifest, or host logs.
        arguments.Add("--env");
        arguments.Add("GEMINI_API_KEY");
        arguments.Add(image);

        var result = await processRunner.RunAsync(
            new ProcessInvocation(
                options.ExecutablePath,
                arguments,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GEMINI_API_KEY"] = request.GeminiApiKey,
                }),
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded("run", result);

        var id = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (id is null || !ContainerIdPattern().IsMatch(id))
        {
            throw new DockerExecutionException("Docker run did not return a valid container ID.");
        }

        return id;
    }

    internal async Task<DockerContainerSnapshot?> TryInspectAsync(
        string containerNameOrId,
        CancellationToken cancellationToken)
    {
        ValidateDockerIdentifier(containerNameOrId);
        var result = await processRunner.RunAsync(
            new ProcessInvocation(
                options.ExecutablePath,
                ["inspect", "--type", "container", "--format", InspectFormat, containerNameOrId]),
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new DockerExecutionException("Docker inspect timed out.");
        }

        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            throw new DockerExecutionException(
                $"Docker inspect failed with exit code {result.ExitCode}.");
        }

        try
        {
            var document = JsonSerializer.Deserialize<InspectDocument>(
                result.StandardOutput,
                DockerJsonOptions);
            return document is null
                ? throw new JsonException("Docker inspect returned an empty document.")
                : ToSnapshot(document);
        }
        catch (JsonException exception)
        {
            throw new DockerExecutionException(
                "Docker inspect returned an invalid document.",
                exception);
        }
    }

    internal async Task<IReadOnlyList<DockerContainerSnapshot>> ListManagedAsync(
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ProcessInvocation(
                options.ExecutablePath,
                [
                    "ps",
                    "--all",
                    "--quiet",
                    "--filter",
                    $"label={SystemLabelName}={SystemLabelValue}",
                ]),
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded("ps", result);

        var snapshots = new List<DockerContainerSnapshot>();
        foreach (var id in result.StandardOutput.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var snapshot = await TryInspectAsync(id, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    internal async Task<int?> WaitAsync(
        string containerName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateDockerIdentifier(containerName);
        var result = await processRunner.RunAsync(
            new ProcessInvocation(options.ExecutablePath, ["wait", containerName]),
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return null;
        }

        EnsureSucceeded("wait", result);
        var output = result.StandardOutput.Trim();
        if (!int.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
        {
            throw new DockerExecutionException("Docker wait did not return an exit code.");
        }

        return exitCode;
    }

    internal Task StopAsync(string containerName, CancellationToken cancellationToken) =>
        RunRequiredAsync(
            "stop",
            [
                "stop",
                "--time",
                options.StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                containerName,
            ],
            options.CommandTimeout + TimeSpan.FromSeconds(options.StopTimeoutSeconds),
            cancellationToken);

    internal Task RemoveAsync(string containerName, CancellationToken cancellationToken) =>
        RunRequiredAsync(
            "rm",
            ["rm", containerName],
            options.CommandTimeout,
            cancellationToken);

    internal Task StreamLogsAsync(
        string containerName,
        string standardOutputPath,
        string standardErrorPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateDockerIdentifier(containerName);
        return RunRequiredAsync(
            "logs",
            ["logs", "--follow", containerName],
            timeout,
            cancellationToken,
            standardOutputPath,
            standardErrorPath);
    }

    private async Task RunRequiredAsync(
        string operation,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardOutputPath = null,
        string? standardErrorPath = null)
    {
        ValidateDockerIdentifier(arguments[^1]);
        var result = await processRunner.RunAsync(
            new ProcessInvocation(
                options.ExecutablePath,
                arguments,
                StandardOutputPath: standardOutputPath,
                StandardErrorPath: standardErrorPath),
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(operation, result);
    }

    private static DockerContainerSnapshot ToSnapshot(InspectDocument document)
    {
        var labels = document.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        labels.TryGetValue(SessionLabelName, out var sessionIdText);
        labels.TryGetValue(ProfileLabelName, out var repositoryProfile);
        Guid.TryParseExact(sessionIdText, "D", out var sessionId);

        return new DockerContainerSnapshot(
            document.Id ?? string.Empty,
            (document.Name ?? string.Empty).TrimStart('/'),
            sessionId,
            repositoryProfile ?? string.Empty,
            document.State ?? "unknown",
            document.ExitCode,
            ParseDockerTimestamp(document.StartedAt),
            ParseDockerTimestamp(document.FinishedAt),
            labels,
            document.Image ?? string.Empty,
            document.User ?? string.Empty,
            document.Init,
            document.NanoCpus,
            document.Memory,
            document.PidsLimit,
            document.StopTimeout,
            document.Mounts ?? []);
    }

    private static DateTimeOffset? ParseDockerTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("0001-", StringComparison.Ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static void AddEnvironment(List<string> arguments, string name, string value)
    {
        ValidateEnvironmentValue(name, value);
        arguments.Add("--env");
        arguments.Add($"{name}={value}");
    }

    private static void ValidateRunRequest(DockerRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDockerIdentifier(request.ContainerName);
        if (request.RepositorySessionId == Guid.Empty)
        {
            throw new ArgumentException("The repository session ID cannot be empty.", nameof(request));
        }

        if (!ProfileIdPattern().IsMatch(request.RepositoryProfile))
        {
            throw new ArgumentException("The repository profile ID is invalid.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ImageReference) ||
            request.ImageReference.Contains('@', StringComparison.Ordinal) ||
            request.ImageReference.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "The image reference must be a tag/reference without a digest.",
                nameof(request));
        }

        if (!ImageDigestPattern().IsMatch(request.ImageDigest))
        {
            throw new ArgumentException(
                "The image digest must be an immutable lowercase sha256 digest.",
                nameof(request));
        }

        if (!Path.IsPathFullyQualified(request.SessionDirectory) ||
            request.SessionDirectory.Contains(',', StringComparison.Ordinal) ||
            ContainsControlCharacter(request.SessionDirectory))
        {
            throw new ArgumentException(
                "The session directory must be an absolute mount-safe path.",
                nameof(request));
        }

        if (request.OpenTelemetryEndpoint is null ||
            !request.OpenTelemetryEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("An absolute OTLP endpoint is required.", nameof(request));
        }

        ValidateEnvironmentValue("TRACEPARENT", request.TraceParent ?? string.Empty);
        ValidateEnvironmentValue("LLM_PROVIDER", request.LlmProvider);
        ValidateEnvironmentValue("LLM_MODEL", request.LlmModel);
        ValidateEnvironmentValue("GEMINI_API_KEY", request.GeminiApiKey);
        if (!request.LlmProvider.Equals("gemini", StringComparison.Ordinal) ||
            !request.LlmModel.Equals("gemini/gemini-3.6-flash", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Version one requires Gemini with model gemini/gemini-3.6-flash.",
                nameof(request));
        }

        if (string.IsNullOrEmpty(request.GeminiApiKey))
        {
            throw new ArgumentException("The Gemini API key is required.", nameof(request));
        }
    }

    private static DockerExecutionOptions ValidateOptions(DockerExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ExecutablePath) ||
            options.CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Docker execution options are invalid.", nameof(options));
        }

        if (options.CpuLimit != 8m ||
            !options.MemoryLimit.Equals("20g", StringComparison.Ordinal) ||
            options.PidsLimit != 4096 ||
            options.StopTimeoutSeconds != 30 ||
            options.ContainerUserId != 10001 ||
            options.ContainerGroupId != 10001)
        {
            throw new ArgumentException(
                "Version one Docker isolation must use 8 CPUs, 20g memory, 4096 PIDs, " +
                "a 30-second stop timeout, and UID/GID 10001:10001.",
                nameof(options));
        }

        return options;
    }

    private static void ValidateEnvironmentValue(string name, string value)
    {
        if (string.IsNullOrEmpty(value) && name is not "TRACEPARENT")
        {
            throw new ArgumentException($"The {name} environment value is required.");
        }

        if (ContainsControlCharacter(value))
        {
            throw new ArgumentException($"The {name} environment value contains a control character.");
        }
    }

    private static bool ContainsControlCharacter(string value) =>
        value.Any(character => character is '\0' or '\r' or '\n');

    private static void ValidateDockerIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            ContainsControlCharacter(value) ||
            value[0] == '-')
        {
            throw new ArgumentException("The Docker container identifier is invalid.", nameof(value));
        }
    }

    private static void EnsureSucceeded(string operation, ProcessExecutionResult result)
    {
        if (result.TimedOut)
        {
            throw new DockerExecutionException($"Docker {operation} timed out.");
        }

        if (result.ExitCode != 0)
        {
            throw new DockerExecutionException(
                $"Docker {operation} failed with exit code {result.ExitCode}.");
        }
    }

    private static readonly JsonSerializerOptions DockerJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [GeneratedRegex("^[a-f0-9]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerIdPattern();

    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageDigestPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdPattern();

    private sealed record InspectDocument(
        string? Id,
        string? Name,
        string? State,
        int ExitCode,
        string? StartedAt,
        string? FinishedAt,
        Dictionary<string, string>? Labels,
        string? Image,
        string? User,
        bool? Init,
        long? NanoCpus,
        long? Memory,
        long? PidsLimit,
        int? StopTimeout,
        IReadOnlyList<InspectMount>? Mounts);

    internal sealed record InspectMount(
        string? Type,
        string? Source,
        string? Destination,
        bool Rw);
}

internal sealed record DockerRunRequest(
    Guid RepositorySessionId,
    string RepositoryProfile,
    string ContainerName,
    string SessionDirectory,
    string ImageReference,
    string ImageDigest,
    Uri OpenTelemetryEndpoint,
    string? TraceParent,
    string LlmProvider,
    string LlmModel,
    string GeminiApiKey);

internal sealed record DockerContainerSnapshot(
    string ContainerId,
    string ContainerName,
    Guid RepositorySessionId,
    string RepositoryProfile,
    string State,
    int ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyDictionary<string, string> Labels,
    string Image,
    string User,
    bool? Init,
    long? NanoCpus,
    long? Memory,
    long? PidsLimit,
    int? StopTimeout,
    IReadOnlyList<DockerContainerClient.InspectMount> Mounts);

public sealed class DockerExecutionException : Exception
{
    public DockerExecutionException(string message)
        : base(message)
    {
    }

    public DockerExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
