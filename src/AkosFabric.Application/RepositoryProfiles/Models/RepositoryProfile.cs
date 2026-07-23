namespace AkosFabric.Application.RepositoryProfiles.Models;

public sealed record RepositoryProfile(
    int SchemaVersion,
    string Id,
    string ProfileRevisionSha,
    JiraRepositoryProfile Jira,
    SourceControlRepositoryProfile SourceControl,
    RepositoryDefinition Repository,
    IReadOnlyList<SupplementalRepositoryDefinition> SupplementalRepositories,
    LlmRepositoryProfile Llm,
    ImageRepositoryProfile Image,
    IReadOnlyList<string> Languages,
    SerenaRepositoryProfile Serena,
    SessionRepositoryProfile Session,
    ItemRepositoryProfile Item,
    IReadOnlyList<ProcessCommand> Bootstrap,
    VerificationRepositoryProfile Verification,
    CiRepositoryProfile Ci);

public sealed record JiraRepositoryProfile(
    string Site,
    string ProjectKey,
    IReadOnlyList<string> EligibleIssueTypes,
    string SelectionJql,
    JiraFieldProfile Fields,
    JiraWorkflowProfile Workflow);

public sealed record JiraFieldProfile(
    string Id,
    string Key,
    string Summary,
    string Description,
    string IssueType,
    string Status,
    string Priority,
    string Labels,
    string Updated);

public sealed record JiraWorkflowProfile(
    string Mode,
    string EligibleStatus,
    string AssignedStatus,
    string ReviewStatus,
    string CompletedStatus,
    string FailedStatus,
    bool AddOutcomeComment);

public sealed record SourceControlRepositoryProfile(
    string Provider,
    Uri BaseUrl,
    string AuthenticationProfile);

public sealed record RepositoryDefinition(
    string ProviderRepositoryId,
    Uri CloneUrl,
    string DefaultBranch,
    string CloneStrategy,
    bool GitLfs,
    string Submodules);

public sealed record SupplementalRepositoryDefinition(
    string ProviderRepositoryId,
    Uri CloneUrl,
    string DefaultBranch,
    string CloneStrategy,
    bool GitLfs,
    string Submodules,
    bool Writable);

public sealed record LlmRepositoryProfile(
    string Provider,
    string ModelId,
    string OpenHandsModel,
    string CredentialProfile);

public sealed record ImageRepositoryProfile(
    string Reference,
    string ExpectedDigest);

public sealed record SerenaRepositoryProfile(
    string Context,
    string ProjectConfiguration);

public sealed record SessionRepositoryProfile(
    int MaxItems,
    int MaxDurationMinutes,
    bool ContinueAfterItemFailure);

public sealed record ItemRepositoryProfile(
    int MaximumCoderConversations,
    int MaximumModelCallsPerRole,
    decimal MaximumCostUsd,
    int MaximumChangedFiles,
    int MaximumDiffLines);

public sealed record ProcessCommand(
    string Name,
    IReadOnlyList<string> Argv,
    int TimeoutSeconds);

public sealed record VerificationRepositoryProfile(
    IReadOnlyList<ProcessCommand> Required);

public sealed record CiRepositoryProfile(
    string Provider,
    ReviewCommand ReviewCommand);

public sealed record ReviewCommand(
    IReadOnlyList<string> Argv);
