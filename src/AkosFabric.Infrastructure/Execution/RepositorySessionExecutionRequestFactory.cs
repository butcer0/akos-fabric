using System.Text.Json;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Infrastructure.Execution;

public sealed class RepositorySessionExecutionRequestFactory
    : IRepositorySessionExecutionRequestFactory
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IRepositorySessionRepository repository;
    private readonly IRepositoryProfileProvider profileProvider;
    private readonly ISourceControlCredentialProviderResolver
        sourceControlCredentials;
    private readonly ILlmApiCredentialProvider llmCredentials;
    private readonly RepositorySessionExecutionRequestFactoryOptions options;

    public RepositorySessionExecutionRequestFactory(
        IRepositorySessionRepository repository,
        IRepositoryProfileProvider profileProvider,
        ISourceControlCredentialProviderResolver sourceControlCredentials,
        ILlmApiCredentialProvider llmCredentials,
        RepositorySessionExecutionRequestFactoryOptions options)
    {
        this.repository =
            repository ?? throw new ArgumentNullException(nameof(repository));
        this.profileProvider =
            profileProvider ??
            throw new ArgumentNullException(nameof(profileProvider));
        this.sourceControlCredentials =
            sourceControlCredentials ??
            throw new ArgumentNullException(nameof(sourceControlCredentials));
        this.llmCredentials =
            llmCredentials ??
            throw new ArgumentNullException(nameof(llmCredentials));
        this.options =
            options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
    }

    public async Task<RepositorySessionExecutionRequest> CreateAsync(
        RepositorySessionRecord session,
        RepositorySessionRequestedV1 message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        if (message.SchemaVersion != 1 ||
            message.RepositorySessionId != session.Id ||
            message.MessageId != session.MessageId)
        {
            throw new InvalidDataException(
                "The repository-session message identity does not match the ledger.");
        }

        RepositoryProfile profile =
            await profileProvider.FindAsync(
                session.RepositoryProfile,
                cancellationToken)
            ?? throw new InvalidDataException(
                $"Repository profile '{session.RepositoryProfile}' was not found.");
        ValidateProfileIdentity(session, profile);

        IReadOnlyList<WorkItemRunRecord> workItems =
            await repository.ListWorkItemsAsync(session.Id, cancellationToken);
        ValidateWorkItems(
            session.Id,
            workItems,
            profile.Session.MaxItems);

        var sourceRepository = new SourceRepositoryReference(
            profile.SourceControl.Provider,
            profile.Repository.ProviderRepositoryId,
            profile.Repository.CloneUrl);
        ISourceControlCredentialProvider credentialProvider =
            sourceControlCredentials.Resolve(
                profile.SourceControl.Provider,
                profile.SourceControl.AuthenticationProfile);
        SourceControlCredential sourceCredential =
            await credentialProvider.GetCredentialAsync(
                sourceRepository,
                new SourceControlPermissionSet(
                    CanReadRepository: true,
                    CanPushBranch: true,
                    CanCreateChangeRequest: false),
                cancellationToken);
        ValidateCredential(sourceCredential);

        string apiKey = await llmCredentials.GetApiKeyAsync(
            profile.Llm.Provider,
            profile.Llm.CredentialProfile,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidDataException(
                "The configured LLM credential profile returned an empty API key.");
        }

        AgentSessionManifestV1 manifest = CreateManifest(
            session,
            profile,
            workItems);
        byte[] manifestJson =
            JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);

        return new RepositorySessionExecutionRequest(
            session.Id,
            session.RepositoryProfile,
            manifestJson,
            sourceCredential,
            session.ImageReference,
            session.ImageDigest,
            options.OpenTelemetryEndpoint,
            message.TraceParent,
            profile.Llm.Provider,
            profile.Llm.OpenHandsModel,
            apiKey);
    }

    private static AgentSessionManifestV1 CreateManifest(
        RepositorySessionRecord session,
        RepositoryProfile profile,
        IReadOnlyList<WorkItemRunRecord> workItems) =>
        new(
            SchemaVersion: 1,
            session.Id,
            session.RepositoryProfile,
            session.ProfileRevisionSha,
            session.ImageDigest,
            new AgentSourceControlV1(
                profile.SourceControl.Provider,
                profile.SourceControl.BaseUrl),
            new AgentRepositoryV1(
                profile.Repository.ProviderRepositoryId,
                profile.Repository.CloneUrl,
                profile.Repository.DefaultBranch,
                profile.Repository.CloneStrategy,
                profile.Repository.GitLfs,
                profile.Repository.Submodules),
            profile.SupplementalRepositories
                .Select(repository => new AgentSupplementalRepositoryV1(
                    repository.ProviderRepositoryId,
                    repository.CloneUrl,
                    repository.DefaultBranch,
                    repository.CloneStrategy,
                    repository.GitLfs,
                    repository.Submodules,
                    repository.Writable))
                .ToArray(),
            new AgentLlmV1(
                profile.Llm.Provider,
                profile.Llm.ModelId,
                profile.Llm.OpenHandsModel),
            workItems
                .OrderBy(item => item.SequenceNumber)
                .Select(item => new AgentWorkItemManifestV1(
                    item.Id,
                    item.SequenceNumber,
                    item.JiraKey,
                    item.JiraUpdatedAt,
                    ParseSnapshot(item)))
                .ToArray(),
            new AgentSessionBehaviorV1(
                profile.Session.ContinueAfterItemFailure),
            new AgentSessionLimitsV1(
                checked(profile.Session.MaxDurationMinutes * 60),
                profile.Session.MaxItems,
                profile.Item.MaximumCostUsd,
                profile.Item.MaximumChangedFiles,
                profile.Item.MaximumDiffLines,
                profile.Item.MaximumCoderConversations,
                profile.Item.MaximumModelCallsPerRole));

    private static JsonElement ParseSnapshot(WorkItemRunRecord item)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(item.JiraSnapshotJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new JsonException(
                    "The Jira snapshot root must be an object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Work-item run '{item.Id}' has an invalid Jira snapshot.",
                exception);
        }
    }

    private static void ValidateProfileIdentity(
        RepositorySessionRecord session,
        RepositoryProfile profile)
    {
        if (!string.Equals(
                session.ProfileRevisionSha,
                profile.ProfileRevisionSha,
                StringComparison.Ordinal) ||
            !string.Equals(
                session.SourceControlProvider,
                profile.SourceControl.Provider,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                session.ImageReference,
                profile.Image.Reference,
                StringComparison.Ordinal) ||
            !string.Equals(
                session.ImageDigest,
                profile.Image.ExpectedDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The loaded repository profile does not match the immutable " +
                "session ledger identity.");
        }
    }

    private static void ValidateWorkItems(
        Guid repositorySessionId,
        IReadOnlyList<WorkItemRunRecord> workItems,
        int maximumItems)
    {
        if (workItems.Count == 0 || workItems.Count > maximumItems)
        {
            throw new InvalidDataException(
                "The session work-item count is outside the repository-profile limit.");
        }

        WorkItemRunRecord[] ordered = workItems
            .OrderBy(item => item.SequenceNumber)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].SequenceNumber != index + 1 ||
                ordered[index].RepositorySessionId != repositorySessionId)
            {
                throw new InvalidDataException(
                    "Session work items must have contiguous sequence numbers " +
                    "and one repository-session identity.");
            }
        }
    }

    private static void ValidateCredential(
        SourceControlCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.Username) ||
            string.IsNullOrEmpty(credential.Secret))
        {
            throw new InvalidDataException(
                "The source-control credential provider returned an invalid credential.");
        }
    }
}
