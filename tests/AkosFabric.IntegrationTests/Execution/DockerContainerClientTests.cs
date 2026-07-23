using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.IntegrationTests.Execution;

public sealed class DockerContainerClientTests
{
    private static readonly Guid SessionId =
        Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c");

    [Fact]
    public async Task RunUsesExactSandboxContractWithoutPuttingGeminiSecretInArguments()
    {
        var runner = new RecordingProcessRunner(
            new ProcessExecutionResult(
                0,
                TimedOut: false,
                $"{new string('b', 64)}{Environment.NewLine}",
                string.Empty));
        var client = new DockerContainerClient(new DockerExecutionOptions(), runner);
        var sessionDirectory = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "akos-docker-command-test", SessionId.ToString("D")));
        var secret = "gemini-test-secret-never-in-argv";

        var id = await client.RunAsync(
            CreateRunRequest(sessionDirectory, secret),
            CancellationToken.None);

        Assert.Equal(new string('b', 64), id);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("docker", invocation.FileName);
        Assert.Equal(
            [
                "run",
                "--detach",
                "--name",
                $"agent-{SessionId:D}",
                "--label",
                "agent.system=autonomous-engineering",
                "--label",
                $"agent.repository-session-id={SessionId:D}",
                "--label",
                "agent.repository-profile=akos-fabric",
                "--init",
                "--user",
                "10001:10001",
                "--cpus",
                "8",
                "--memory",
                "20g",
                "--pids-limit",
                "4096",
                "--stop-timeout",
                "30",
                "--mount",
                $"type=bind,src={sessionDirectory},dst=/run/agent",
                "--env",
                $"AGENT_SESSION_ID={SessionId:D}",
                "--env",
                "TASK_MANIFEST=/run/agent/manifest.json",
                "--env",
                "RESULT_PATH=/run/agent/result.json",
                "--env",
                "SOURCE_CONTROL_CREDENTIAL_PATH=/run/agent/source-control-credential.json",
                "--env",
                "OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317/",
                "--env",
                "TRACEPARENT=00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                "--env",
                "LLM_PROVIDER=gemini",
                "--env",
                "LLM_MODEL=gemini/gemini-3.6-flash",
                "--env",
                "GEMINI_API_KEY",
                $"registry.example/akos-agent:1.4@sha256:{new string('a', 64)}",
            ],
            invocation.Arguments);
        Assert.DoesNotContain(invocation.Arguments, argument => argument.Contains(secret, StringComparison.Ordinal));
        Assert.Equal(secret, Assert.Contains("GEMINI_API_KEY", invocation.Environment!));
        Assert.Single(invocation.Arguments, argument => argument == "--mount");
        Assert.DoesNotContain(
            invocation.Arguments,
            argument => argument.Contains("docker.sock", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task RunRejectsNonImmutableImageDigest(string digest)
    {
        var runner = new RecordingProcessRunner();
        var client = new DockerContainerClient(new DockerExecutionOptions(), runner);
        var request = CreateRunRequest(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "akos-digest-test")),
            "secret") with
        {
            ImageDigest = digest,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.RunAsync(request, CancellationToken.None));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void RejectsWeakenedVersionOneIsolationLimits()
    {
        Assert.Throws<ArgumentException>(
            () => new DockerContainerClient(
                new DockerExecutionOptions { PidsLimit = 1024 }));
    }

    [Fact]
    public async Task LifecycleCommandsAreShellFreeAndBounded()
    {
        var runner = new RecordingProcessRunner(
            new ProcessExecutionResult(0, false, "17\n", string.Empty),
            new ProcessExecutionResult(0, false, string.Empty, string.Empty),
            new ProcessExecutionResult(0, false, string.Empty, string.Empty));
        var client = new DockerContainerClient(new DockerExecutionOptions(), runner);
        var name = $"agent-{SessionId:D}";

        var exitCode = await client.WaitAsync(name, TimeSpan.FromMinutes(1), CancellationToken.None);
        await client.StopAsync(name, CancellationToken.None);
        await client.RemoveAsync(name, CancellationToken.None);

        Assert.Equal(17, exitCode);
        Assert.Collection(
            runner.Invocations,
            wait => Assert.Equal(["wait", name], wait.Arguments),
            stop => Assert.Equal(["stop", "--time", "30", name], stop.Arguments),
            remove => Assert.Equal(["rm", name], remove.Arguments));
        Assert.All(runner.Timeouts, timeout => Assert.True(timeout > TimeSpan.Zero));
    }

    [Fact]
    public async Task WaitReturnsNullWhenDeadlineExpires()
    {
        var runner = new RecordingProcessRunner(
            new ProcessExecutionResult(null, TimedOut: true, string.Empty, string.Empty));
        var client = new DockerContainerClient(new DockerExecutionOptions(), runner);

        var exitCode = await client.WaitAsync(
            $"agent-{SessionId:D}",
            TimeSpan.FromSeconds(4),
            CancellationToken.None);

        Assert.Null(exitCode);
        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(runner.Timeouts));
    }

    [Fact]
    public async Task ListUsesTheOneBoundedRecoveryLabelScan()
    {
        var inspectJson = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                id = new string('c', 64),
                name = $"/agent-{SessionId:D}",
                state = "running",
                exitCode = 0,
                startedAt = "2026-07-23T12:00:00Z",
                finishedAt = "0001-01-01T00:00:00Z",
                labels = new Dictionary<string, string>
                {
                    ["agent.system"] = "autonomous-engineering",
                    ["agent.repository-session-id"] = SessionId.ToString("D"),
                    ["agent.repository-profile"] = "akos-fabric",
                },
            });
        var runner = new RecordingProcessRunner(
            new ProcessExecutionResult(0, false, $"{new string('c', 64)}\n", string.Empty),
            new ProcessExecutionResult(0, false, inspectJson, string.Empty));
        var client = new DockerContainerClient(new DockerExecutionOptions(), runner);

        var containers = await client.ListManagedAsync(CancellationToken.None);

        var container = Assert.Single(containers);
        Assert.Equal(SessionId, container.RepositorySessionId);
        Assert.Equal("akos-fabric", container.RepositoryProfile);
        Assert.Equal(
            [
                "ps",
                "--all",
                "--quiet",
                "--filter",
                "label=agent.system=autonomous-engineering",
            ],
            runner.Invocations[0].Arguments);
        Assert.Equal("inspect", runner.Invocations[1].Arguments[0]);
        Assert.DoesNotContain(
            runner.Invocations[1].Arguments,
            argument => argument.Contains(".Config.Env", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealDockerRecoveryScanIsReadOnlyAndOptIn()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AKOS_RUN_DOCKER_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var client = new DockerContainerClient(new DockerExecutionOptions());
        var containers = await client.ListManagedAsync(CancellationToken.None);
        Assert.NotNull(containers);
    }

    private static DockerRunRequest CreateRunRequest(
        string sessionDirectory,
        string geminiApiKey) =>
        new(
            SessionId,
            "akos-fabric",
            $"agent-{SessionId:D}",
            sessionDirectory,
            "registry.example/akos-agent:1.4",
            $"sha256:{new string('a', 64)}",
            new Uri("http://127.0.0.1:4317"),
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            "gemini",
            "gemini/gemini-3.6-flash",
            geminiApiKey);

    private sealed class RecordingProcessRunner(
        params ProcessExecutionResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessExecutionResult> results = new(results);

        public List<ProcessInvocation> Invocations { get; } = [];

        public List<TimeSpan> Timeouts { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            Timeouts.Add(timeout);
            if (results.Count == 0)
            {
                throw new InvalidOperationException("No process result was configured.");
            }

            return Task.FromResult(results.Dequeue());
        }
    }
}
