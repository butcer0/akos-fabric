using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.Jira.Services;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.UnitTests.Jira;

public sealed class JiraSelectionServiceTests
{
    [Fact]
    public async Task QueriesEveryProfileAndCreatesFirstBoundedEligibleGroup()
    {
        RepositoryProfile first = CreateProfile("akos-fabric", "KAN", 2);
        RepositoryProfile second = CreateProfile("operations", "OPS", 1);
        var profiles = new FakeProfileProvider(first, second);
        var jira = new FakeJiraClient();
        jira.Results[first.Id] =
        [
            Issue("KAN-1"),
            Issue("KAN-2"),
            Issue("KAN-3", description: " "),
            Issue("KAN-4", issueType: "Task"),
            Issue("KAN-5", status: "In Progress"),
            Issue("KAN-6"),
            Issue("KAN-7"),
        ];
        jira.Results[second.Id] = [Issue("OPS-1")];
        var selectionRepository = new FakeSelectionRepository(
            activeChecks: [false, false],
            nonTerminalKeys: ["KAN-2"]);
        var sessions = new FakeRepositorySessionService();
        var service = new JiraSelectionService(
            profiles,
            jira,
            selectionRepository,
            sessions);

        JiraSelectionResult result = await service.SelectAsync(
            [" akos-fabric ", "operations"],
            CancellationToken.None);

        Assert.Equal(JiraSelectionOutcome.SessionCreated, result.Outcome);
        Assert.Equal(2, result.ProfilesQueried);
        Assert.Equal(3, result.EligibleCandidateCount);
        Assert.Equal(first.Id, result.RepositoryProfile);
        Assert.Equal(sessions.CreatedSessionId, result.RepositorySessionId);
        Assert.Equal([first.Id, second.Id], profiles.RequestedProfiles);
        Assert.Equal(
            [first.Jira.SelectionJql, second.Jira.SelectionJql],
            jira.SelectionJqls);
        Assert.Equal(first.Id, sessions.Input!.RepositoryProfile);
        Assert.Equal(["KAN-1", "KAN-6"], sessions.Input.JiraKeys);
        Assert.Equal(
            JiraSelectionService.ServiceSubject,
            sessions.Caller!.Subject);
        Assert.Equal(
            JiraSelectionService.ServiceSubject,
            sessions.Caller.ClientId);
        Assert.Null(sessions.Caller.TokenId);
        Assert.Matches("^[0-9a-f]{32}$", sessions.Caller.TraceId!);
        Assert.Matches(
            "^00-[0-9a-f]{32}-[0-9a-f]{16}-01$",
            sessions.Caller.TraceParent);
    }

    [Fact]
    public async Task ActiveSessionSkipsProfileAndJiraReads()
    {
        var profiles = new FakeProfileProvider(
            CreateProfile("akos-fabric", "KAN", 2));
        var jira = new FakeJiraClient();
        var sessions = new FakeRepositorySessionService();
        var service = new JiraSelectionService(
            profiles,
            jira,
            new FakeSelectionRepository([true], []),
            sessions);

        JiraSelectionResult result = await service.SelectAsync(
            ["akos-fabric"],
            CancellationToken.None);

        Assert.Equal(JiraSelectionOutcome.ActiveSession, result.Outcome);
        Assert.Equal(0, result.ProfilesQueried);
        Assert.Empty(profiles.RequestedProfiles);
        Assert.Empty(jira.SelectionJqls);
        Assert.Null(sessions.Input);
    }

    [Fact]
    public async Task SessionCreatedDuringJiraReadsConsumesCapacity()
    {
        RepositoryProfile profile = CreateProfile("akos-fabric", "KAN", 2);
        var profiles = new FakeProfileProvider(profile);
        var jira = new FakeJiraClient();
        jira.Results[profile.Id] = [Issue("KAN-1")];
        var sessions = new FakeRepositorySessionService();
        var service = new JiraSelectionService(
            profiles,
            jira,
            new FakeSelectionRepository([false, true], []),
            sessions);

        JiraSelectionResult result = await service.SelectAsync(
            [profile.Id],
            CancellationToken.None);

        Assert.Equal(JiraSelectionOutcome.ActiveSession, result.Outcome);
        Assert.Equal(1, result.ProfilesQueried);
        Assert.Equal(1, result.EligibleCandidateCount);
        Assert.Null(sessions.Input);
    }

    [Fact]
    public async Task NoEligibleCandidatesDoesNotCreateSession()
    {
        RepositoryProfile profile = CreateProfile("akos-fabric", "KAN", 2);
        var jira = new FakeJiraClient();
        jira.Results[profile.Id] =
        [
            Issue("KAN-1", description: ""),
            Issue("KAN-2", status: "Done"),
        ];
        var sessions = new FakeRepositorySessionService();
        var service = new JiraSelectionService(
            new FakeProfileProvider(profile),
            jira,
            new FakeSelectionRepository([false], []),
            sessions);

        JiraSelectionResult result = await service.SelectAsync(
            [profile.Id],
            CancellationToken.None);

        Assert.Equal(JiraSelectionOutcome.NoCandidates, result.Outcome);
        Assert.Equal(1, result.ProfilesQueried);
        Assert.Null(sessions.Input);
    }

    [Fact]
    public async Task DuplicateEnabledProfileIsRejectedBeforeReads()
    {
        RepositoryProfile profile = CreateProfile("akos-fabric", "KAN", 2);
        var repository = new FakeSelectionRepository([false], []);
        var service = new JiraSelectionService(
            new FakeProfileProvider(profile),
            new FakeJiraClient(),
            repository,
            new FakeRepositorySessionService());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SelectAsync(
                ["akos-fabric", "AKOS-FABRIC"],
                CancellationToken.None));

        Assert.Equal(0, repository.ActiveCheckCount);
    }

    private static JiraIssueSnapshot Issue(
        string key,
        string description = "Complete requirements",
        string issueType = "Story",
        string status = "To Do") =>
        new(
            key,
            key,
            $"Issue {key}",
            description,
            issueType,
            status,
            "High",
            [],
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            $$"""{"id":"{{key}}","key":"{{key}}"}""");

    private sealed class FakeProfileProvider(params RepositoryProfile[] profiles)
        : IRepositoryProfileProvider
    {
        private readonly IReadOnlyDictionary<string, RepositoryProfile> values =
            profiles.ToDictionary(profile => profile.Id);

        public List<string> RequestedProfiles { get; } = [];

        public Task<RepositoryProfile?> FindAsync(
            string profileName,
            CancellationToken cancellationToken)
        {
            RequestedProfiles.Add(profileName);
            return Task.FromResult<RepositoryProfile?>(
                values.GetValueOrDefault(profileName));
        }
    }

    private sealed class FakeJiraClient : IJiraClient
    {
        public Dictionary<string, IReadOnlyList<JiraIssueSnapshot>> Results
        {
            get;
        } = [];

        public List<string> SelectionJqls { get; } = [];

        public Task<IReadOnlyList<JiraIssueSnapshot>> SearchIssuesAsync(
            JiraRepositoryProfile profile,
            CancellationToken cancellationToken)
        {
            SelectionJqls.Add(profile.SelectionJql);
            string profileId = profile.ProjectKey == "KAN"
                ? "akos-fabric"
                : "operations";
            return Task.FromResult(
                Results.GetValueOrDefault(profileId)
                ?? (IReadOnlyList<JiraIssueSnapshot>)[]);
        }

        public Task<JiraIssueSnapshot?> FindIssueAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JiraTransitionResult> TransitionIssueAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            JiraWorkflowTarget workflowTarget,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddCommentAsync(
            JiraRepositoryProfile profile,
            string issueKey,
            string comment,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSelectionRepository(
        IEnumerable<bool> activeChecks,
        IReadOnlyList<string> nonTerminalKeys)
        : IJiraSelectionRepository
    {
        private readonly Queue<bool> activeChecks = new(activeChecks);

        public int ActiveCheckCount { get; private set; }

        public Task<bool> HasActiveRepositorySessionAsync(
            CancellationToken cancellationToken)
        {
            ActiveCheckCount++;
            return Task.FromResult(activeChecks.Dequeue());
        }

        public Task<IReadOnlyList<string>> ListNonTerminalJiraKeysAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(nonTerminalKeys);
    }

    private sealed class FakeRepositorySessionService
        : IRepositorySessionService
    {
        public Guid CreatedSessionId { get; } = Guid.NewGuid();
        public CreateRepositorySessionInput? Input { get; private set; }
        public RepositorySessionCaller? Caller { get; private set; }

        public Task<RepositorySessionDetails> CreateAsync(
            CreateRepositorySessionInput input,
            RepositorySessionCaller caller,
            CancellationToken cancellationToken)
        {
            Input = input;
            Caller = caller;
            return Task.FromResult(
                new RepositorySessionDetails(
                    new RepositorySessionRecord(
                        CreatedSessionId,
                        input.RepositoryProfile,
                        new string('a', 40),
                        "github",
                        "agent:1",
                        $"sha256:{new string('b', 64)}",
                        RepositorySessionStatus.Published,
                        Guid.NewGuid(),
                        null,
                        null,
                        "{}",
                        caller.Subject,
                        caller.ClientId,
                        caller.TokenId,
                        caller.TraceId,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        null,
                        null,
                        null,
                        null),
                    []));
        }

        public Task<RepositorySessionDetails> PublishAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionDetails> RetryAsync(
            Guid repositorySessionId,
            RepositorySessionCaller caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionDetails> CancelAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionDetails> GetAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemRunRecord>> ListItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static RepositoryProfile CreateProfile(
        string id,
        string projectKey,
        int maxItems) =>
        new(
            1,
            id,
            new string('a', 40),
            new JiraRepositoryProfile(
                "default",
                projectKey,
                ["Story", "Bug"],
                $"project = {projectKey} ORDER BY priority DESC",
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
                new Uri("https://api.github.com"),
                "default"),
            new RepositoryDefinition(
                $"owner/{id}",
                new Uri($"https://github.com/owner/{id}.git"),
                "main",
                "full",
                false,
                "none"),
            [],
            new LlmRepositoryProfile(
                "gemini",
                "model",
                "gemini/model",
                "default"),
            new ImageRepositoryProfile(
                "agent:1",
                $"sha256:{new string('b', 64)}"),
            ["csharp"],
            new SerenaRepositoryProfile("ide-assistant", ".serena/project.yml"),
            new SessionRepositoryProfile(maxItems, 60, false),
            new ItemRepositoryProfile(2, 10, 5, 20, 1000),
            [],
            new VerificationRepositoryProfile([]),
            new CiRepositoryProfile(
                "github-actions",
                new ReviewCommand(["review"])));
}
