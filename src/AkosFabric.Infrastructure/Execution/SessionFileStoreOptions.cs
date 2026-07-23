namespace AkosFabric.Infrastructure.Execution;

public sealed class SessionFileStoreOptions
{
    public required string RootDirectory { get; init; }

    public int OwnerUserId { get; init; } = 10001;

    public int OwnerGroupId { get; init; } = 10001;
}
