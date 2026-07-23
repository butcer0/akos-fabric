using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Domain.WorkItems;

namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record AgentResultRecording(
    Guid RepositorySessionId,
    DateTimeOffset OccurredAt,
    IReadOnlyList<AgentWorkItemOutcomeRecording> WorkItems);

public sealed record AgentWorkItemOutcomeRecording(
    Guid WorkItemRunId,
    WorkItemRunStatus Status,
    string? BaseCommitSha,
    string? BranchName,
    string? CandidateCommitSha,
    string? PlanJson,
    string? CandidateJson,
    string? VerificationJson,
    string? JudgmentJson,
    string ModelUsageJson,
    string? FailureCode,
    string? FailureMessage,
    string LedgerPayloadJson);

public sealed record ChangeRequestRecording(
    Guid RepositorySessionId,
    Guid WorkItemRunId,
    string BranchName,
    string CandidateCommitSha,
    ChangeRequestReference ChangeRequest,
    DateTimeOffset OccurredAt);

public sealed record AgentResultProcessingFailure(
    Guid RepositorySessionId,
    string FailureCode,
    string FailureMessage,
    string PayloadJson,
    DateTimeOffset OccurredAt);

public sealed record AgentResultProcessingCompletion(
    Guid RepositorySessionId,
    RepositorySessionStatus FinalStatus,
    string? FailureCode,
    string? FailureMessage,
    string PayloadJson,
    DateTimeOffset OccurredAt);

public sealed record JiraSynchronizationWarningRecording(
    Guid RepositorySessionId,
    Guid? WorkItemRunId,
    string Operation,
    string FailureCode,
    string PayloadJson,
    DateTimeOffset OccurredAt);
