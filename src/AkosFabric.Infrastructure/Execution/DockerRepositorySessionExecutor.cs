using System.Diagnostics;
using System.Text.Json;
using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.Telemetry;

namespace AkosFabric.Infrastructure.Execution;

public sealed class DockerRepositorySessionExecutor : IRepositorySessionExecutor
{
    private readonly SessionFileStore sessionFileStore;
    private readonly DockerContainerClient containerClient;

    public DockerRepositorySessionExecutor(
        SessionFileStore sessionFileStore,
        DockerContainerClient containerClient)
    {
        this.sessionFileStore =
            sessionFileStore ?? throw new ArgumentNullException(nameof(sessionFileStore));
        this.containerClient =
            containerClient ?? throw new ArgumentNullException(nameof(containerClient));
    }

    public async Task<RepositorySessionExecution> StartAsync(
        RepositorySessionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateManifestIdentity(request);
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.DockerContainerStart,
            ActivityKind.Client,
            correlation: new ControlCorrelation(
                request.RepositorySessionId));

        var sessionDirectory = sessionFileStore.EnsureSessionDirectory(request.RepositorySessionId);
        await sessionFileStore.WriteManifestAsync(
            request.RepositorySessionId,
            request.ManifestJson,
            cancellationToken).ConfigureAwait(false);
        await sessionFileStore.ReplaceCredentialAsync(
            request.RepositorySessionId,
            request.SourceControlCredential,
            cancellationToken).ConfigureAwait(false);
        await sessionFileStore.InitializeLogFilesAsync(
            request.RepositorySessionId,
            cancellationToken).ConfigureAwait(false);

        var containerName = GetContainerName(request.RepositorySessionId);
        var existing = await containerClient.TryInspectAsync(
            containerName,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateExistingContainer(existing, request, sessionDirectory);
            return ToExecution(existing, sessionDirectory, reattached: true);
        }

        var runRequest = new DockerRunRequest(
            request.RepositorySessionId,
            request.RepositoryProfile,
            containerName,
            sessionDirectory,
            request.ImageReference,
            request.ImageDigest,
            request.OpenTelemetryEndpoint,
            request.TraceParent,
            request.LlmProvider,
            request.LlmModel,
            request.GeminiApiKey);

        try
        {
            var containerId = await containerClient.RunAsync(
                runRequest,
                cancellationToken).ConfigureAwait(false);
            return new RepositorySessionExecution(
                request.RepositorySessionId,
                containerName,
                containerId,
                sessionDirectory,
                Reattached: false);
        }
        catch (DockerExecutionException)
        {
            // A second control-plane instance can win the deterministic-name race.
            existing = await containerClient.TryInspectAsync(
                containerName,
                cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            ValidateExistingContainer(existing, request, sessionDirectory);
            return ToExecution(existing, sessionDirectory, reattached: true);
        }
    }

    public async Task<RepositorySessionContainer?> InspectAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await containerClient.TryInspectAsync(
            GetContainerName(repositorySessionId),
            cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : ToApplicationContainer(snapshot);
    }

    public async Task<RepositorySessionWaitResult> WaitAsync(
        Guid repositorySessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.DockerContainerWait,
            ActivityKind.Client,
            correlation: new ControlCorrelation(repositorySessionId));

        var exitCode = await containerClient.WaitAsync(
            GetContainerName(repositorySessionId),
            timeout,
            cancellationToken).ConfigureAwait(false);
        return new RepositorySessionWaitResult(
            TimedOut: exitCode is null,
            ExitCode: exitCode);
    }

    public Task StreamLogsAsync(
        Guid repositorySessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var stdout = sessionFileStore.GetSessionFile(
            repositorySessionId,
            SessionFileStore.StandardOutputFileName);
        var stderr = sessionFileStore.GetSessionFile(
            repositorySessionId,
            SessionFileStore.StandardErrorFileName);
        return containerClient.StreamLogsAsync(
            GetContainerName(repositorySessionId),
            stdout,
            stderr,
            timeout,
            cancellationToken);
    }

    public Task ReplaceCredentialAsync(
        Guid repositorySessionId,
        SourceControlCredential credential,
        CancellationToken cancellationToken) =>
        sessionFileStore.ReplaceCredentialAsync(
            repositorySessionId,
            credential,
            cancellationToken);

    public Task DeleteCredentialAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sessionFileStore.DeleteCredential(repositorySessionId);
        return Task.CompletedTask;
    }

    public async Task StopAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        var name = GetContainerName(repositorySessionId);
        if (await containerClient.TryInspectAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            await containerClient.StopAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        var name = GetContainerName(repositorySessionId);
        if (await containerClient.TryInspectAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            await containerClient.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RepositorySessionContainer>> ListManagedAsync(
        CancellationToken cancellationToken)
    {
        var snapshots = await containerClient.ListManagedAsync(cancellationToken).ConfigureAwait(false);
        return snapshots.Select(ToApplicationContainer).ToArray();
    }

    internal static string GetContainerName(Guid repositorySessionId)
    {
        if (repositorySessionId == Guid.Empty)
        {
            throw new ArgumentException("The repository session ID cannot be empty.", nameof(repositorySessionId));
        }

        return $"agent-{repositorySessionId:D}";
    }

    private static void ValidateManifestIdentity(RepositorySessionExecutionRequest request)
    {
        using var document = JsonDocument.Parse(request.ManifestJson);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.GetInt32() != 1 ||
            !root.TryGetProperty("repositorySessionId", out var sessionId) ||
            sessionId.GetString() != request.RepositorySessionId.ToString("D") ||
            !root.TryGetProperty("repositoryProfile", out var profile) ||
            profile.GetString() != request.RepositoryProfile ||
            !root.TryGetProperty("imageDigest", out var imageDigest) ||
            imageDigest.GetString() != request.ImageDigest)
        {
            throw new JsonException(
                "The manifest identity must match the requested session, profile, image digest, and schema version.");
        }
    }

    private static void ValidateExistingContainer(
        DockerContainerSnapshot existing,
        RepositorySessionExecutionRequest request,
        string sessionDirectory)
    {
        var mountedSource = existing.Mounts.Count == 1 &&
                            !string.IsNullOrWhiteSpace(existing.Mounts[0].Source)
            ? Path.GetFullPath(existing.Mounts[0].Source!)
            : null;

        if (existing.RepositorySessionId != request.RepositorySessionId ||
            !existing.RepositoryProfile.Equals(request.RepositoryProfile, StringComparison.Ordinal) ||
            !existing.Labels.TryGetValue(
                DockerContainerClient.SystemLabelName,
                out var systemLabel) ||
            !systemLabel.Equals(
                DockerContainerClient.SystemLabelValue,
                StringComparison.Ordinal) ||
            !existing.Image.Equals(
                $"{request.ImageReference}@{request.ImageDigest}",
                StringComparison.Ordinal) ||
            !existing.User.Equals("10001:10001", StringComparison.Ordinal) ||
            existing.Init is not true ||
            existing.NanoCpus != 8_000_000_000 ||
            existing.Memory != 20L * 1024 * 1024 * 1024 ||
            existing.PidsLimit != 4096 ||
            existing.StopTimeout != 30 ||
            existing.Mounts.Count != 1 ||
            !string.Equals(existing.Mounts[0].Type, "bind", StringComparison.Ordinal) ||
            !string.Equals(
                existing.Mounts[0].Destination,
                "/run/agent",
                StringComparison.Ordinal) ||
            !existing.Mounts[0].Rw ||
            !string.Equals(
                mountedSource,
                Path.GetFullPath(sessionDirectory),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new DockerExecutionException(
                "The deterministic container name is already owned by a different workload.");
        }
    }

    private static RepositorySessionExecution ToExecution(
        DockerContainerSnapshot snapshot,
        string sessionDirectory,
        bool reattached) =>
        new(
            snapshot.RepositorySessionId,
            snapshot.ContainerName,
            snapshot.ContainerId,
            sessionDirectory,
            reattached);

    private static RepositorySessionContainer ToApplicationContainer(
        DockerContainerSnapshot snapshot) =>
        new(
            snapshot.RepositorySessionId,
            snapshot.RepositoryProfile,
            snapshot.ContainerName,
            snapshot.ContainerId,
            ParseState(snapshot.State),
            snapshot.ExitCode,
            snapshot.StartedAt,
            snapshot.FinishedAt);

    private static RepositorySessionContainerState ParseState(string state) =>
        state.ToLowerInvariant() switch
        {
            "created" => RepositorySessionContainerState.Created,
            "restarting" => RepositorySessionContainerState.Restarting,
            "running" => RepositorySessionContainerState.Running,
            "removing" => RepositorySessionContainerState.Removing,
            "paused" => RepositorySessionContainerState.Paused,
            "exited" => RepositorySessionContainerState.Exited,
            "dead" => RepositorySessionContainerState.Dead,
            _ => RepositorySessionContainerState.Unknown,
        };
}
