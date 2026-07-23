namespace AkosFabric.Application.SourceControl.Models;

public sealed record SourceRepositoryReference(
    string Provider,
    string ProviderRepositoryId,
    Uri CloneUrl);
