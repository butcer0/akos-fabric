using AkosFabric.Domain.WorkItems;

namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record WorkItemRunRecord(
    Guid Id,
    Guid RepositorySessionId,
    int SequenceNumber,
    string JiraIssueId,
    string JiraKey,
    DateTimeOffset JiraUpdatedAt,
    string JiraSnapshotJson,
    WorkItemRunStatus Status,
    string? BaseCommitSha,
    string? BranchName,
    string? CandidateCommitSha,
    string? ChangeRequestId,
    string? ChangeRequestNumber,
    string? ChangeRequestUrl,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureMessage);
