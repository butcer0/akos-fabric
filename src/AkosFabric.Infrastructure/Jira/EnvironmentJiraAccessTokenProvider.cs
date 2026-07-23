using AkosFabric.Application.Jira.Interfaces;

namespace AkosFabric.Infrastructure.Jira;

public sealed record JiraEnvironmentCredentialBinding(
    string AuthenticationProfile,
    string EnvironmentVariable);

public sealed class EnvironmentJiraAccessTokenProvider
    : IJiraAccessTokenProvider
{
    private readonly Dictionary<string, string> environmentVariables;
    private readonly Func<string, string?> readEnvironmentVariable;

    public EnvironmentJiraAccessTokenProvider(
        IEnumerable<JiraEnvironmentCredentialBinding> bindings,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        environmentVariables = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (JiraEnvironmentCredentialBinding binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.AuthenticationProfile) ||
                string.IsNullOrWhiteSpace(binding.EnvironmentVariable) ||
                binding.EnvironmentVariable.Any(
                    character => !(
                        char.IsAsciiLetterOrDigit(character) ||
                        character == '_')) ||
                !environmentVariables.TryAdd(
                    binding.AuthenticationProfile,
                    binding.EnvironmentVariable))
            {
                throw new ArgumentException(
                    "Jira credential bindings require unique non-empty " +
                    "profiles and valid environment-variable names.",
                    nameof(bindings));
            }
        }

        this.readEnvironmentVariable =
            readEnvironmentVariable ??
            Environment.GetEnvironmentVariable;
    }

    public ValueTask<string> GetAccessTokenAsync(
        string authenticationProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!environmentVariables.TryGetValue(
                authenticationProfile,
                out string? environmentVariable))
        {
            throw new InvalidOperationException(
                $"Jira authentication profile '{authenticationProfile}' is " +
                "not registered.");
        }

        string? token = readEnvironmentVariable(environmentVariable);
        return ValueTask.FromResult(
            !string.IsNullOrWhiteSpace(token)
                ? token
                : throw new InvalidOperationException(
                    $"Environment variable '{environmentVariable}' for Jira " +
                    "authentication profile is unset or empty."));
    }
}
