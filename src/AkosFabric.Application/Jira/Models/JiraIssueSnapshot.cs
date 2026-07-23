namespace AkosFabric.Application.Jira.Models;

public sealed record JiraIssueSnapshot(
    string IssueId,
    string Key,
    string Summary,
    string Description,
    string IssueType,
    string Status,
    string? Priority,
    IReadOnlyList<string> Labels,
    DateTimeOffset UpdatedAt,
    string SnapshotJson);
