namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record RepositorySessionCreation(
    Guid Id,
    string RepositoryProfile,
    string ProfileRevisionSha,
    string SourceControlProvider,
    string ImageReference,
    string ImageDigest,
    Guid MessageId,
    string RequestPayloadJson,
    string RequestedBySubject,
    string RequestedByClientId,
    string? RequestedByTokenId,
    string? TraceId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WorkItemRunCreation> WorkItems);
