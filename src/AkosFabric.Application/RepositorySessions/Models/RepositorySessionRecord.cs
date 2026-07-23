using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record RepositorySessionRecord(
    Guid Id,
    string RepositoryProfile,
    string ProfileRevisionSha,
    string SourceControlProvider,
    string ImageReference,
    string ImageDigest,
    RepositorySessionStatus Status,
    Guid MessageId,
    string? ContainerName,
    string? ContainerId,
    string RequestPayloadJson,
    string RequestedBySubject,
    string RequestedByClientId,
    string? RequestedByTokenId,
    string? TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureMessage);
