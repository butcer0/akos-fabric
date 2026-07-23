using System.Text.Json;

namespace AkosFabric.Application.AgentExecution.Models;

public sealed record AgentSessionArtifactsV1(
    AgentSessionManifestV1 Manifest,
    AgentSessionResultV1 Result,
    string ResultJson,
    IReadOnlyDictionary<Guid, string> ItemResultJson);

public sealed record AgentSessionManifestV1(
    int SchemaVersion,
    Guid RepositorySessionId,
    string RepositoryProfile,
    string ProfileRevisionSha,
    string ImageDigest,
    AgentSourceControlV1 SourceControl,
    AgentRepositoryV1 MainRepository,
    IReadOnlyList<AgentSupplementalRepositoryV1> SupplementalRepositories,
    AgentLlmV1 Llm,
    IReadOnlyList<AgentWorkItemManifestV1> WorkItems,
    AgentSessionBehaviorV1 SessionBehavior,
    AgentSessionLimitsV1 Limits);

public sealed record AgentSourceControlV1(
    string Provider,
    Uri BaseUrl);

public sealed record AgentRepositoryV1(
    string ProviderRepositoryId,
    Uri CloneUrl,
    string DefaultBranch,
    string CloneStrategy,
    bool GitLfs,
    string Submodules);

public sealed record AgentSupplementalRepositoryV1(
    string ProviderRepositoryId,
    Uri CloneUrl,
    string DefaultBranch,
    string CloneStrategy,
    bool GitLfs,
    string Submodules,
    bool Writable);

public sealed record AgentLlmV1(
    string Provider,
    string ModelId,
    string OpenHandsModel);

public sealed record AgentWorkItemManifestV1(
    Guid WorkItemRunId,
    int SequenceNumber,
    string JiraKey,
    DateTimeOffset JiraUpdatedAt,
    JsonElement JiraSnapshot);

public sealed record AgentSessionBehaviorV1(
    bool ContinueAfterItemFailure);

public sealed record AgentSessionLimitsV1(
    int SessionDeadlineSeconds,
    int MaximumItems,
    decimal MaximumCostUsdPerItem,
    int MaximumChangedFiles,
    int MaximumDiffLines,
    int MaximumCoderConversations,
    int MaximumModelCallsPerRole);

public sealed record AgentSessionResultV1(
    int SchemaVersion,
    Guid RepositorySessionId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? FailureCode,
    string? FailureMessage,
    AgentResultRepositoryV1 Repository,
    AgentResultLlmV1 Llm,
    IReadOnlyList<AgentWorkItemResultV1> Items);

public sealed record AgentResultRepositoryV1(
    string Provider,
    string ProviderRepositoryId,
    Uri CloneUrl);

public sealed record AgentResultLlmV1(
    string Provider,
    string ModelId);

public sealed record AgentWorkItemResultV1(
    Guid WorkItemRunId,
    string JiraKey,
    string Status,
    string? BaseCommitSha,
    string? BranchName,
    string? CandidateCommitSha,
    IReadOnlyList<string> ChangedFiles,
    JsonElement? Plan,
    JsonElement? Candidate,
    JsonElement? Verification,
    JsonElement? Judgment,
    AgentModelUsageV1 ModelUsage,
    string? FailureCode,
    string? FailureMessage);

public sealed record AgentModelUsageV1(
    string Provider,
    string ModelId,
    JsonElement Planner,
    JsonElement Coder,
    JsonElement Judge,
    decimal TotalEstimatedCostUsd);

public sealed class AgentResultValidationException : Exception
{
    public AgentResultValidationException(string message)
        : base(message)
    {
    }

    public AgentResultValidationException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
