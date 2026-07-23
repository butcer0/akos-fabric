using System.Diagnostics;

using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;

namespace AkosFabric.Application.Jira.Services;

public sealed class JiraSelectionService : IJiraSelectionService, IDisposable
{
    public const string ServiceSubject = "service:jira-selection-worker";

    private readonly IRepositoryProfileProvider profileProvider;
    private readonly IJiraClient jiraClient;
    private readonly IJiraSelectionRepository selectionRepository;
    private readonly IRepositorySessionService repositorySessionService;
    private readonly SemaphoreSlim pollGate = new(1, 1);

    public JiraSelectionService(
        IRepositoryProfileProvider profileProvider,
        IJiraClient jiraClient,
        IJiraSelectionRepository selectionRepository,
        IRepositorySessionService repositorySessionService)
    {
        this.profileProvider = profileProvider
            ?? throw new ArgumentNullException(nameof(profileProvider));
        this.jiraClient = jiraClient
            ?? throw new ArgumentNullException(nameof(jiraClient));
        this.selectionRepository = selectionRepository
            ?? throw new ArgumentNullException(nameof(selectionRepository));
        this.repositorySessionService = repositorySessionService
            ?? throw new ArgumentNullException(nameof(repositorySessionService));
    }

    public async Task<JiraSelectionResult> SelectAsync(
        IReadOnlyList<string> enabledRepositoryProfiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enabledRepositoryProfiles);
        await pollGate.WaitAsync(cancellationToken);
        try
        {
            return await SelectCoreAsync(
                NormalizeProfileNames(enabledRepositoryProfiles),
                cancellationToken);
        }
        finally
        {
            pollGate.Release();
        }
    }

    public void Dispose()
    {
        pollGate.Dispose();
    }

    private async Task<JiraSelectionResult> SelectCoreAsync(
        List<string> profileNames,
        CancellationToken cancellationToken)
    {
        if (await selectionRepository.HasActiveRepositorySessionAsync(
                cancellationToken))
        {
            return new JiraSelectionResult(
                JiraSelectionOutcome.ActiveSession,
                0,
                0,
                null,
                null);
        }

        IReadOnlyList<string> nonTerminalKeys =
            await selectionRepository.ListNonTerminalJiraKeysAsync(
                cancellationToken);
        var excludedKeys = new HashSet<string>(
            nonTerminalKeys,
            StringComparer.OrdinalIgnoreCase);
        var selections = new List<ProfileSelection>(profileNames.Count);
        var seenCandidateKeys = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        int eligibleCandidateCount = 0;

        foreach (string profileName in profileNames)
        {
            RepositoryProfile profile =
                await profileProvider.FindAsync(profileName, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Enabled repository profile '{profileName}' was not found.");
            IReadOnlyList<JiraIssueSnapshot> issues =
                await jiraClient.SearchIssuesAsync(
                    profile.Jira,
                    cancellationToken);
            string[] eligible = issues
                .Where(issue => IsEligible(profile, issue))
                .Where(issue => !excludedKeys.Contains(issue.Key))
                .Where(issue => seenCandidateKeys.Add(issue.Key))
                .Select(issue => issue.Key)
                .Take(profile.Session.MaxItems)
                .ToArray();
            eligibleCandidateCount += eligible.Length;
            selections.Add(new ProfileSelection(profile.Id, eligible));
        }

        if (eligibleCandidateCount == 0)
        {
            return new JiraSelectionResult(
                JiraSelectionOutcome.NoCandidates,
                profileNames.Count,
                0,
                null,
                null);
        }

        // Recheck after the remote Jira reads so a session created while those
        // requests were in flight consumes the runner's single-session capacity.
        if (await selectionRepository.HasActiveRepositorySessionAsync(
                cancellationToken))
        {
            return new JiraSelectionResult(
                JiraSelectionOutcome.ActiveSession,
                profileNames.Count,
                eligibleCandidateCount,
                null,
                null);
        }

        ProfileSelection selected = selections.First(
            selection => selection.JiraKeys.Count > 0);
        RepositorySessionDetails created =
            await repositorySessionService.CreateAsync(
                new CreateRepositorySessionInput(
                    selected.RepositoryProfile,
                    selected.JiraKeys),
                CreateCaller(),
                cancellationToken);
        return new JiraSelectionResult(
            JiraSelectionOutcome.SessionCreated,
            profileNames.Count,
            eligibleCandidateCount,
            selected.RepositoryProfile,
            created.Session.Id);
    }

    private static List<string> NormalizeProfileNames(
        IReadOnlyList<string> profileNames)
    {
        var normalized = new List<string>(profileNames.Count);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? profileName in profileNames)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new ArgumentException(
                    "Enabled repository profile names must be non-empty.",
                    nameof(profileNames));
            }

            string value = profileName.Trim();
            if (!unique.Add(value))
            {
                throw new ArgumentException(
                    $"Repository profile '{value}' was enabled more than once.",
                    nameof(profileNames));
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static bool IsEligible(
        RepositoryProfile profile,
        JiraIssueSnapshot issue) =>
        !string.IsNullOrWhiteSpace(issue.Key)
        && !string.IsNullOrWhiteSpace(issue.Description)
        && profile.Jira.EligibleIssueTypes.Contains(
            issue.IssueType,
            StringComparer.OrdinalIgnoreCase)
        && string.Equals(
            issue.Status,
            profile.Jira.Workflow.EligibleStatus,
            StringComparison.OrdinalIgnoreCase);

    private static RepositorySessionCaller CreateCaller()
    {
        ActivityTraceId traceId = Activity.Current?.TraceId
                                  ?? ActivityTraceId.CreateRandom();
        ActivitySpanId spanId = Activity.Current?.SpanId
                                ?? ActivitySpanId.CreateRandom();
        return new RepositorySessionCaller(
            ServiceSubject,
            ServiceSubject,
            null,
            traceId.ToString(),
            $"00-{traceId}-{spanId}-01");
    }

    private sealed record ProfileSelection(
        string RepositoryProfile,
        IReadOnlyList<string> JiraKeys);
}
