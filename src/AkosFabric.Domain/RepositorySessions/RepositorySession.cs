using System.Collections.Frozen;

namespace AkosFabric.Domain.RepositorySessions;

public sealed class RepositorySession
{
    private static readonly FrozenDictionary<RepositorySessionStatus, FrozenSet<RepositorySessionStatus>>
        AllowedTransitions =
            new Dictionary<RepositorySessionStatus, FrozenSet<RepositorySessionStatus>>
            {
                [RepositorySessionStatus.Created] = Set(
                    RepositorySessionStatus.Published,
                    RepositorySessionStatus.Starting,
                    RepositorySessionStatus.Failed,
                    RepositorySessionStatus.Cancelled),
                [RepositorySessionStatus.Published] = Set(
                    RepositorySessionStatus.Starting,
                    RepositorySessionStatus.Failed,
                    RepositorySessionStatus.Cancelled),
                [RepositorySessionStatus.Starting] = Set(
                    RepositorySessionStatus.Running,
                    RepositorySessionStatus.Failed,
                    RepositorySessionStatus.Cancelled),
                [RepositorySessionStatus.Running] = Set(
                    RepositorySessionStatus.ProcessingResults,
                    RepositorySessionStatus.Failed,
                    RepositorySessionStatus.Cancelled),
                [RepositorySessionStatus.ProcessingResults] = Set(
                    RepositorySessionStatus.Completed,
                    RepositorySessionStatus.Failed,
                    RepositorySessionStatus.Cancelled),
                [RepositorySessionStatus.Completed] = Set(),
                [RepositorySessionStatus.Failed] = Set(),
                [RepositorySessionStatus.Cancelled] = Set(),
            }.ToFrozenDictionary();

    public RepositorySession(Guid id, string repositoryProfile, Guid messageId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A repository session ID is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(repositoryProfile))
        {
            throw new ArgumentException("A repository profile is required.", nameof(repositoryProfile));
        }

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A message ID is required.", nameof(messageId));
        }

        Id = id;
        RepositoryProfile = repositoryProfile;
        MessageId = messageId;
        Status = RepositorySessionStatus.Created;
    }

    public Guid Id { get; }

    public string RepositoryProfile { get; }

    public Guid MessageId { get; }

    public RepositorySessionStatus Status { get; private set; }

    public bool IsTerminal =>
        Status is RepositorySessionStatus.Completed
            or RepositorySessionStatus.Failed
            or RepositorySessionStatus.Cancelled;

    public void TransitionTo(RepositorySessionStatus nextStatus)
    {
        if (!AllowedTransitions[Status].Contains(nextStatus))
        {
            throw new InvalidOperationException(
                $"Repository session cannot transition from {Status} to {nextStatus}.");
        }

        Status = nextStatus;
    }

    private static FrozenSet<RepositorySessionStatus> Set(
        params RepositorySessionStatus[] statuses) =>
        statuses.ToFrozenSet();
}
