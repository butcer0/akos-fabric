using System.Text;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.IntegrationTests.Execution;

public sealed class DockerRepositorySessionExecutorTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "akos-docker-executor-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartCreatesFilesAndUsesDeterministicContainerIdentity()
    {
        var sessionId = Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c");
        var containerId = new string('d', 64);
        var runner = new QueueProcessRunner(
            new ProcessExecutionResult(1, false, string.Empty, "No such object"),
            new ProcessExecutionResult(0, false, $"{containerId}\n", string.Empty));
        var fileStore = CreateFileStore();
        var executor = new DockerRepositorySessionExecutor(
            fileStore,
            new DockerContainerClient(new DockerExecutionOptions(), runner));
        var request = CreateRequest(sessionId);

        var execution = await executor.StartAsync(request, CancellationToken.None);

        Assert.Equal($"agent-{sessionId:D}", execution.ContainerName);
        Assert.Equal(containerId, execution.ContainerId);
        Assert.False(execution.Reattached);
        Assert.Equal(fileStore.GetSessionDirectory(sessionId), execution.SessionDirectory);
        Assert.True(File.Exists(fileStore.GetSessionFile(sessionId, "manifest.json")));
        Assert.True(
            File.Exists(fileStore.GetSessionFile(sessionId, "source-control-credential.json")));
        Assert.Equal("inspect", runner.Invocations[0].Arguments[0]);
        Assert.Equal("run", runner.Invocations[1].Arguments[0]);
    }

    [Fact]
    public async Task StartReattachesOnlyWhenAllOwnershipLabelsMatch()
    {
        var sessionId = Guid.Parse("b711ecb1-0eb9-4fed-bf37-c24ac99c8ca1");
        var containerId = new string('e', 64);
        var sessionDirectory = Path.Combine(rootDirectory, sessionId.ToString("D"));
        var inspect = CreateInspectJson(sessionId, containerId, sessionDirectory);
        var runner = new QueueProcessRunner(
            new ProcessExecutionResult(0, false, inspect, string.Empty));
        var executor = new DockerRepositorySessionExecutor(
            CreateFileStore(),
            new DockerContainerClient(new DockerExecutionOptions(), runner));

        var execution = await executor.StartAsync(
            CreateRequest(sessionId),
            CancellationToken.None);

        Assert.True(execution.Reattached);
        Assert.Equal(containerId, execution.ContainerId);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task StartRejectsLookalikeContainerWithWrongImmutableImage()
    {
        var sessionId = Guid.NewGuid();
        var sessionDirectory = Path.Combine(rootDirectory, sessionId.ToString("D"));
        var inspect = CreateInspectJson(
            sessionId,
            new string('f', 64),
            sessionDirectory,
            image: $"registry.example/akos-agent:other@sha256:{new string('9', 64)}");
        var runner = new QueueProcessRunner(
            new ProcessExecutionResult(0, false, inspect, string.Empty));
        var executor = new DockerRepositorySessionExecutor(
            CreateFileStore(),
            new DockerContainerClient(new DockerExecutionOptions(), runner));

        await Assert.ThrowsAsync<DockerExecutionException>(
            () => executor.StartAsync(CreateRequest(sessionId), CancellationToken.None));
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task StartRejectsManifestWhoseIdentityDoesNotMatchRequest()
    {
        var sessionId = Guid.NewGuid();
        var request = CreateRequest(sessionId) with
        {
            ManifestJson = Encoding.UTF8.GetBytes(
                $$"""
                {"schemaVersion":1,"repositorySessionId":"{{Guid.NewGuid():D}}","repositoryProfile":"akos-fabric","imageDigest":"sha256:{{new string('a', 64)}}"}
                """),
        };
        var runner = new QueueProcessRunner();
        var executor = new DockerRepositorySessionExecutor(
            CreateFileStore(),
            new DockerContainerClient(new DockerExecutionOptions(), runner));

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => executor.StartAsync(request, CancellationToken.None));
        Assert.Empty(runner.Invocations);
        Assert.False(Directory.Exists(Path.Combine(rootDirectory, sessionId.ToString("D"))));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SessionFileStore CreateFileStore() =>
        new(
            new SessionFileStoreOptions
            {
                RootDirectory = rootDirectory,
                OwnerUserId = SessionFileStore.GetEffectiveUserId(),
                OwnerGroupId = SessionFileStore.GetEffectiveGroupId(),
            });

    private static RepositorySessionExecutionRequest CreateRequest(Guid sessionId)
    {
        var digest = $"sha256:{new string('a', 64)}";
        var manifest = Encoding.UTF8.GetBytes(
            $$"""
            {"schemaVersion":1,"repositorySessionId":"{{sessionId:D}}","repositoryProfile":"akos-fabric","imageDigest":"{{digest}}"}
            """);
        return new RepositorySessionExecutionRequest(
            sessionId,
            "akos-fabric",
            manifest,
            new SourceControlCredential(
                "x-access-token",
                "source-control-secret",
                DateTimeOffset.UtcNow.AddMinutes(5)),
            "registry.example/akos-agent:1.4",
            digest,
            new Uri("http://127.0.0.1:4317"),
            null,
            "gemini",
            "gemini/gemini-3.6-flash",
            "gemini-secret");
    }

    private static string CreateInspectJson(
        Guid sessionId,
        string containerId,
        string sessionDirectory,
        string? image = null) =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                id = containerId,
                name = $"/agent-{sessionId:D}",
                state = "running",
                exitCode = 0,
                startedAt = "2026-07-23T12:00:00Z",
                finishedAt = "0001-01-01T00:00:00Z",
                labels = new Dictionary<string, string>
                {
                    ["agent.system"] = "autonomous-engineering",
                    ["agent.repository-session-id"] = sessionId.ToString("D"),
                    ["agent.repository-profile"] = "akos-fabric",
                },
                image = image ??
                        $"registry.example/akos-agent:1.4@sha256:{new string('a', 64)}",
                user = "10001:10001",
                init = true,
                nanoCpus = 8_000_000_000,
                memory = 20L * 1024 * 1024 * 1024,
                pidsLimit = 4096,
                stopTimeout = 30,
                mounts = new[]
                {
                    new
                    {
                        type = "bind",
                        source = sessionDirectory,
                        destination = "/run/agent",
                        rw = true,
                    },
                },
            });

    private sealed class QueueProcessRunner(
        params ProcessExecutionResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessExecutionResult> results = new(results);

        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            if (results.Count == 0)
            {
                throw new InvalidOperationException("No result was configured.");
            }

            return Task.FromResult(results.Dequeue());
        }
    }
}
