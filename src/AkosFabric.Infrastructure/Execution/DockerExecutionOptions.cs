namespace AkosFabric.Infrastructure.Execution;

public sealed class DockerExecutionOptions
{
    public string ExecutablePath { get; init; } = "docker";

    public decimal CpuLimit { get; init; } = 8m;

    public string MemoryLimit { get; init; } = "20g";

    public int PidsLimit { get; init; } = 4096;

    public int StopTimeoutSeconds { get; init; } = 30;

    public int ContainerUserId { get; init; } = 10001;

    public int ContainerGroupId { get; init; } = 10001;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
