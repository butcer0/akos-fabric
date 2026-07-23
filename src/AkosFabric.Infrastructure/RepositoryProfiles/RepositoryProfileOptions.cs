namespace AkosFabric.Infrastructure.RepositoryProfiles;

public sealed class RepositoryProfileOptions
{
    public const string SectionName = "RepositoryProfiles";

    public string RootPath { get; init; } = string.Empty;

    public string SchemaPath { get; init; } = string.Empty;
}
