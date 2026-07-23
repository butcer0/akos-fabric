using System.Text.Json.Serialization;

using AkosFabric.Application.RepositorySessions.Models;

namespace AkosFabric.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateRepositorySessionRequest(
    string RepositoryProfile,
    IReadOnlyList<string> JiraKeys);

public sealed record RepositorySessionResponse(
    Guid Id,
    string RepositoryProfile,
    string ProfileRevisionSha,
    string SourceControlProvider,
    string ImageReference,
    string ImageDigest,
    string Status,
    Guid MessageId,
    string? ContainerName,
    string? ContainerId,
    string RequestedBySubject,
    string RequestedByClientId,
    string? RequestedByTokenId,
    string? TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureMessage)
{
    public static RepositorySessionResponse From(RepositorySessionRecord record) =>
        new(
            record.Id,
            record.RepositoryProfile,
            record.ProfileRevisionSha,
            record.SourceControlProvider,
            record.ImageReference,
            record.ImageDigest,
            StatusNames.RepositorySession(record.Status),
            record.MessageId,
            record.ContainerName,
            record.ContainerId,
            record.RequestedBySubject,
            record.RequestedByClientId,
            record.RequestedByTokenId,
            record.TraceId,
            record.CreatedAt,
            record.PublishedAt,
            record.StartedAt,
            record.CompletedAt,
            record.FailureCode,
            record.FailureMessage);
}

public sealed record WorkItemRunResponse(
    Guid Id,
    Guid RepositorySessionId,
    int SequenceNumber,
    string JiraIssueId,
    string JiraKey,
    DateTimeOffset JiraUpdatedAt,
    string Status,
    string? BaseCommitSha,
    string? BranchName,
    string? CandidateCommitSha,
    string? ChangeRequestId,
    string? ChangeRequestNumber,
    string? ChangeRequestUrl,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureMessage)
{
    public static WorkItemRunResponse From(WorkItemRunRecord record) =>
        new(
            record.Id,
            record.RepositorySessionId,
            record.SequenceNumber,
            record.JiraIssueId,
            record.JiraKey,
            record.JiraUpdatedAt,
            StatusNames.WorkItem(record.Status),
            record.BaseCommitSha,
            record.BranchName,
            record.CandidateCommitSha,
            record.ChangeRequestId,
            record.ChangeRequestNumber,
            record.ChangeRequestUrl,
            record.StartedAt,
            record.CompletedAt,
            record.FailureCode,
            record.FailureMessage);
}

public sealed record RepositorySessionDetailsResponse(
    RepositorySessionResponse Session,
    IReadOnlyList<WorkItemRunResponse> Items)
{
    public static RepositorySessionDetailsResponse From(
        RepositorySessionDetails details) =>
        new(
            RepositorySessionResponse.From(details.Session),
            details.WorkItems.Select(WorkItemRunResponse.From).ToArray());
}
