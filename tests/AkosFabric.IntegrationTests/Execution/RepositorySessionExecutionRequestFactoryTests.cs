using System.Text.Json;

using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Infrastructure.Execution;
using AkosFabric.Infrastructure.SourceControl;

using Json.Schema;

namespace AkosFabric.IntegrationTests.Execution;

public sealed class RepositorySessionExecutionRequestFactoryTests
{
    private const string ProfileRevision =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ImageDigest =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task CreatesStrictSecretFreeManifestAndExternalCredentials()
    {
        Guid sessionId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        RepositoryProfile profile = CreateProfile();
        RepositorySessionRecord session =
            CreateSession(sessionId, messageId);
        WorkItemRunRecord[] items =
        [
            CreateWorkItem(sessionId, sequenceNumber: 2, jiraKey: "KAN-2"),
            CreateWorkItem(sessionId, sequenceNumber: 1, jiraKey: "KAN-1"),
        ];
        var repository = new FakeRepository(items);
        var sourceCredentials = new CapturingCredentialResolver(
            new SourceControlCredential(
                "x-access-token",
                "source-secret-canary",
                DateTimeOffset.UtcNow.AddMinutes(30)));
        var llmCredentials = new FakeLlmCredentialProvider(
            "gemini-secret-canary");
        var factory = new RepositorySessionExecutionRequestFactory(
            repository,
            new FakeProfileProvider(profile),
            sourceCredentials,
            llmCredentials,
            new RepositorySessionExecutionRequestFactoryOptions
            {
                OpenTelemetryEndpoint =
                    new Uri("http://otel-collector:4317"),
            });
        var message = new RepositorySessionRequestedV1(
            1,
            messageId,
            sessionId,
            "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01",
            DateTimeOffset.UtcNow);

        var request = await factory.CreateAsync(
            session,
            message,
            CancellationToken.None);

        Assert.Equal("source-secret-canary", request.SourceControlCredential.Secret);
        Assert.Equal("gemini-secret-canary", request.GeminiApiKey);
        Assert.Equal("gemini", request.LlmProvider);
        Assert.Equal("gemini/gemini-3.6-flash", request.LlmModel);
        Assert.Equal(
            new Uri("http://otel-collector:4317"),
            request.OpenTelemetryEndpoint);
        Assert.Equal(
            new SourceControlPermissionSet(true, true, false),
            sourceCredentials.Permissions);
        Assert.Equal(
            ("github", "akos-fabric-source-control"),
            sourceCredentials.ResolvedBinding);
        Assert.Equal(
            ("gemini", "gemini-development"),
            llmCredentials.ResolvedBinding);

        string manifestText =
            System.Text.Encoding.UTF8.GetString(request.ManifestJson.Span);
        Assert.DoesNotContain("source-secret-canary", manifestText);
        Assert.DoesNotContain("gemini-secret-canary", manifestText);
        Assert.DoesNotContain("credentialProfile", manifestText);
        Assert.DoesNotContain("authenticationProfile", manifestText);

        using JsonDocument document = JsonDocument.Parse(request.ManifestJson);
        JsonElement root = document.RootElement;
        JsonSchema schema = JsonSchema.FromFile(
            Path.Combine(
                FindRepositoryRoot(),
                "schemas",
                "agent-session-manifest-v1.schema.json"),
            new BuildOptions
            {
                Dialect = Dialect.Draft202012,
                SchemaRegistry = new SchemaRegistry(),
            });
        EvaluationResults evaluation = schema.Evaluate(
            root,
            new EvaluationOptions
            {
                RequireFormatValidation = true,
            });
        Assert.True(evaluation.IsValid);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            sessionId,
            root.GetProperty("repositorySessionId").GetGuid());
        Assert.Equal(
            ProfileRevision,
            root.GetProperty("profileRevisionSha").GetString());
        Assert.Equal(
            ImageDigest,
            root.GetProperty("imageDigest").GetString());
        JsonElement[] manifestItems =
            root.GetProperty("workItems").EnumerateArray().ToArray();
        Assert.Equal(["KAN-1", "KAN-2"], manifestItems
            .Select(item => item.GetProperty("jiraKey").GetString()));
        Assert.Equal(
            14_400,
            root.GetProperty("limits")
                .GetProperty("sessionDeadlineSeconds")
                .GetInt32());
    }

    [Fact]
    public async Task RejectsProfileThatDiffersFromImmutableLedgerIdentity()
    {
        Guid sessionId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        RepositoryProfile profile = CreateProfile() with
        {
            ProfileRevisionSha =
                "cccccccccccccccccccccccccccccccccccccccc",
        };
        var factory = new RepositorySessionExecutionRequestFactory(
            new FakeRepository(
            [
                CreateWorkItem(sessionId, 1, "KAN-1"),
            ]),
            new FakeProfileProvider(profile),
            new CapturingCredentialResolver(
                new SourceControlCredential("user", "secret", null)),
            new FakeLlmCredentialProvider("api-key"),
            new RepositorySessionExecutionRequestFactoryOptions());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => factory.CreateAsync(
                CreateSession(sessionId, messageId),
                new RepositorySessionRequestedV1(
                    1,
                    messageId,
                    sessionId,
                    "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01",
                    DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public void ResolvesCredentialOnlyForExactNamedBinding()
    {
        var provider = new FakeSourceControlCredentialProvider(
            new SourceControlCredential("user", "secret", null));
        var resolver = new SourceControlCredentialProviderResolver(
        [
            new SourceControlCredentialProviderRegistration(
                "github",
                "production-app",
                provider),
        ]);

        Assert.Same(
            provider,
            resolver.Resolve("GITHUB", "production-app"));
        Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve("github", "other-app"));
    }

    [Fact]
    public async Task ReadsLlmSecretFromNamedEnvironmentBinding()
    {
        var provider = new EnvironmentLlmApiCredentialProvider(
        [
            new LlmEnvironmentCredentialBinding(
                "gemini",
                "gemini-development",
                "GEMINI_API_KEY"),
        ],
        variable => variable == "GEMINI_API_KEY"
            ? "external-secret"
            : null);

        string secret = await provider.GetApiKeyAsync(
            "GEMINI",
            "gemini-development",
            CancellationToken.None);

        Assert.Equal("external-secret", secret);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetApiKeyAsync(
                "gemini",
                "unknown-profile",
                CancellationToken.None));
    }

    private static RepositoryProfile CreateProfile() =>
        new(
            1,
            "akos-fabric",
            ProfileRevision,
            new JiraRepositoryProfile(
                "default",
                "KAN",
                ["Story", "Bug"],
                "project = KAN",
                new JiraFieldProfile(
                    "id",
                    "key",
                    "summary",
                    "description",
                    "issuetype",
                    "status",
                    "priority",
                    "labels",
                    "updated"),
                new JiraWorkflowProfile(
                    "default-zero-configuration",
                    "To Do",
                    "In Progress",
                    "In Progress",
                    "Done",
                    "To Do",
                    true)),
            new SourceControlRepositoryProfile(
                "github",
                new Uri("https://github.com"),
                "akos-fabric-source-control"),
            new RepositoryDefinition(
                "butcer0/akos-fabric",
                new Uri("https://github.com/butcer0/akos-fabric.git"),
                "main",
                "full",
                false,
                "none"),
            [],
            new LlmRepositoryProfile(
                "gemini",
                "gemini-3.6-flash",
                "gemini/gemini-3.6-flash",
                "gemini-development"),
            new ImageRepositoryProfile(
                "akos-fabric/repository-agent:development",
                ImageDigest),
            ["csharp", "python"],
            new SerenaRepositoryProfile(
                "ide",
                "/opt/repository-profile/serena-project.yml"),
            new SessionRepositoryProfile(5, 240, true),
            new ItemRepositoryProfile(2, 60, 25m, 30, 3000),
            [],
            new VerificationRepositoryProfile([]),
            new CiRepositoryProfile(
                "github-actions",
                new ReviewCommand(
                    ["python", "-m", "agent_runtime.ci_review"])));

    private static RepositorySessionRecord CreateSession(
        Guid sessionId,
        Guid messageId) =>
        new(
            sessionId,
            "akos-fabric",
            ProfileRevision,
            "github",
            "akos-fabric/repository-agent:development",
            ImageDigest,
            RepositorySessionStatus.Published,
            messageId,
            null,
            null,
            "{}",
            "subject",
            "client",
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null);

    private static WorkItemRunRecord CreateWorkItem(
        Guid sessionId,
        int sequenceNumber,
        string jiraKey) =>
        new(
            Guid.NewGuid(),
            sessionId,
            sequenceNumber,
            sequenceNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            jiraKey,
            DateTimeOffset.Parse(
                "2026-07-23T14:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            $$"""{"key":"{{jiraKey}}","summary":"Implement the requirement"}""",
            AkosFabric.Domain.WorkItems.WorkItemRunStatus.Queued,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "schemas",
                        "agent-session-manifest-v1.schema.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Cannot locate the Akos Fabric repository root.");
    }

    private sealed class FakeProfileProvider(RepositoryProfile profile)
        : IRepositoryProfileProvider
    {
        public Task<RepositoryProfile?> FindAsync(
            string profileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RepositoryProfile?>(
                profileName == profile.Id ? profile : null);
        }
    }

    private sealed class CapturingCredentialResolver(
        SourceControlCredential credential)
        : ISourceControlCredentialProviderResolver
    {
        public (string Provider, string Profile) ResolvedBinding { get; private set; }

        public SourceControlPermissionSet? Permissions =>
            Provider.Permissions;

        private FakeSourceControlCredentialProvider Provider { get; } =
            new(credential);

        public ISourceControlCredentialProvider Resolve(
            string providerName,
            string authenticationProfile)
        {
            ResolvedBinding = (providerName, authenticationProfile);
            return Provider;
        }
    }

    private sealed class FakeSourceControlCredentialProvider(
        SourceControlCredential credential)
        : ISourceControlCredentialProvider
    {
        public string ProviderName => "github";

        public SourceControlPermissionSet? Permissions { get; private set; }

        public Task<SourceControlCredential> GetCredentialAsync(
            SourceRepositoryReference repository,
            SourceControlPermissionSet permissions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Permissions = permissions;
            return Task.FromResult(credential);
        }
    }

    private sealed class FakeLlmCredentialProvider(string apiKey)
        : ILlmApiCredentialProvider
    {
        public (string Provider, string Profile) ResolvedBinding { get; private set; }

        public Task<string> GetApiKeyAsync(
            string providerName,
            string credentialProfile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedBinding = (providerName, credentialProfile);
            return Task.FromResult(apiKey);
        }
    }

    private sealed class FakeRepository(
        IReadOnlyList<WorkItemRunRecord> workItems)
        : IRepositorySessionRepository
    {
        public Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(workItems);
        }

        public Task<RepositorySessionRecord?> FindAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionRecord>> ListRecoverableAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateAsync(
            RepositorySessionCreation session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            RepositorySessionCancellation cancellation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionSessionStatusAsync(
            RepositorySessionStatusTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionWorkItemStatusAsync(
            WorkItemRunStatusTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordValidatedResultAsync(
            AgentResultRecording result,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordChangeRequestAsync(
            ChangeRequestRecording recording,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordJiraSynchronizationWarningAsync(
            JiraSynchronizationWarningRecording recording,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailResultProcessingAsync(
            AgentResultProcessingFailure failure,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteResultProcessingAsync(
            AgentResultProcessingCompletion completion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
