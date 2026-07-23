namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record WorkItemRunCreation(
    Guid Id,
    int SequenceNumber,
    string JiraIssueId,
    string JiraKey,
    DateTimeOffset JiraUpdatedAt,
    string JiraSnapshotJson);
