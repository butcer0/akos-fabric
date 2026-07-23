using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Application.SourceControl.Services;
using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.UnitTests.SourceControl;

public sealed class SourceControlCredentialAcquisitionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcquiresPushCredentialFromProfileBinding()
    {
        var provider = new FakeCredentialProvider(
            new SourceControlCredential(
                "x-access-token",
                "provider-secret",
                Now.AddHours(1)));
        var resolver = new FakeResolver(provider);
        var service = new SourceControlCredentialAcquisitionService(
            resolver,
            new FixedTimeProvider(Now));
        RepositoryProfile profile = Profile();

        SourceControlCredential credential =
            await service.AcquireForSessionAsync(
                Session(),
                profile,
                CancellationToken.None);

        Assert.Equal("github", resolver.ProviderName);
        Assert.Equal(
            "source-control",
            resolver.AuthenticationProfile);
        Assert.Equal(
            new SourceRepositoryReference(
                "github",
                "example/akos-fabric",
                new Uri(
                    "https://github.test/example/akos-fabric.git")),
            provider.Repository);
        Assert.Equal(
            new SourceControlPermissionSet(
                CanReadRepository: true,
                CanPushBranch: true,
                CanCreateChangeRequest: false),
            provider.Permissions);
        Assert.Same(provider.Credential, credential);
    }

    [Fact]
    public async Task RejectsProfileThatDoesNotMatchDurableSession()
    {
        var provider = new FakeCredentialProvider(
            new SourceControlCredential(
                "x-access-token",
                "provider-secret",
                Now.AddHours(1)));
        var service = new SourceControlCredentialAcquisitionService(
            new FakeResolver(provider),
            new FixedTimeProvider(Now));
        RepositoryProfile mismatched = Profile() with
        {
            ProfileRevisionSha = new string('c', 40),
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.AcquireForSessionAsync(
                Session(),
                mismatched,
                CancellationToken.None));

        Assert.Null(provider.Repository);
    }

    [Fact]
    public async Task RejectsExpiredCredentialWithoutDisclosingSecret()
    {
        const string secret = "expired-provider-secret";
        var provider = new FakeCredentialProvider(
            new SourceControlCredential(
                "x-access-token",
                secret,
                Now));
        var service = new SourceControlCredentialAcquisitionService(
            new FakeResolver(provider),
            new FixedTimeProvider(Now));

        InvalidDataException exception =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.AcquireForSessionAsync(
                    Session(),
                    Profile(),
                    CancellationToken.None));

        Assert.DoesNotContain(
            secret,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static RepositorySessionRecord Session() =>
        new(
            Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c"),
            "akos-fabric",
            new string('a', 40),
            "github",
            "akos-fabric-agent:development",
            $"sha256:{new string('b', 64)}",
            RepositorySessionStatus.Running,
            Guid.NewGuid(),
            "agent-6a92a62a-1e93-4b5b-a52c-dcc541fb591c",
            new string('d', 64),
            "{}",
            "test-subject",
            "test-client",
            null,
            null,
            Now.AddHours(-1),
            Now.AddHours(-1),
            Now.AddMinutes(-30),
            null,
            null,
            null);

    private static RepositoryProfile Profile() =>
        new(
            1,
            "akos-fabric",
            new string('a', 40),
            new JiraRepositoryProfile(
                "default",
                "KAN",
                ["Story"],
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
                new Uri("https://github.test"),
                "source-control"),
            new RepositoryDefinition(
                "example/akos-fabric",
                new Uri(
                    "https://github.test/example/akos-fabric.git"),
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
                "akos-fabric-agent:development",
                $"sha256:{new string('b', 64)}"),
            ["csharp"],
            new SerenaRepositoryProfile(
                "ide",
                "/opt/repository-profile/serena-project.yml"),
            new SessionRepositoryProfile(5, 240, true),
            new ItemRepositoryProfile(2, 60, 25, 30, 3000),
            [],
            new VerificationRepositoryProfile([]),
            new CiRepositoryProfile(
                "github-actions",
                new ReviewCommand(
                    ["python", "-m", "agent_runtime.ci_review"])));

    private sealed class FakeResolver(
        ISourceControlCredentialProvider provider)
        : ISourceControlCredentialProviderResolver
    {
        public string? ProviderName { get; private set; }

        public string? AuthenticationProfile { get; private set; }

        public ISourceControlCredentialProvider Resolve(
            string providerName,
            string authenticationProfile)
        {
            ProviderName = providerName;
            AuthenticationProfile = authenticationProfile;
            return provider;
        }
    }

    private sealed class FakeCredentialProvider(
        SourceControlCredential credential)
        : ISourceControlCredentialProvider
    {
        public SourceControlCredential Credential => credential;

        public string ProviderName => "github";

        public SourceRepositoryReference? Repository { get; private set; }

        public SourceControlPermissionSet? Permissions { get; private set; }

        public Task<SourceControlCredential> GetCredentialAsync(
            SourceRepositoryReference repository,
            SourceControlPermissionSet permissions,
            CancellationToken cancellationToken)
        {
            Repository = repository;
            Permissions = permissions;
            return Task.FromResult(credential);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
