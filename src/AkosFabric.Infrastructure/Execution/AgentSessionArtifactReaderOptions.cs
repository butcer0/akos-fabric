namespace AkosFabric.Infrastructure.Execution;

public sealed class AgentSessionArtifactReaderOptions
{
    public string ManifestSchemaPath { get; init; } = string.Empty;

    public string ResultSchemaPath { get; init; } = string.Empty;
}
