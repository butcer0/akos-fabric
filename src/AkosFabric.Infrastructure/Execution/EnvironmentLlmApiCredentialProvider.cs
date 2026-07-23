using AkosFabric.Application.AgentExecution.Interfaces;

namespace AkosFabric.Infrastructure.Execution;

public sealed record LlmEnvironmentCredentialBinding(
    string ProviderName,
    string CredentialProfile,
    string EnvironmentVariable);

public sealed class EnvironmentLlmApiCredentialProvider
    : ILlmApiCredentialProvider
{
    private readonly Dictionary<
        (string Provider, string Profile),
        string> environmentVariables;
    private readonly Func<string, string?> readEnvironmentVariable;

    public EnvironmentLlmApiCredentialProvider(
        IEnumerable<LlmEnvironmentCredentialBinding> bindings,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var resolved = new Dictionary<
            (string Provider, string Profile),
            string>(CredentialBindingComparer.Instance);
        foreach (LlmEnvironmentCredentialBinding binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.ProviderName) ||
                string.IsNullOrWhiteSpace(binding.CredentialProfile) ||
                string.IsNullOrWhiteSpace(binding.EnvironmentVariable) ||
                binding.EnvironmentVariable.Any(
                    character => !(
                        char.IsAsciiLetterOrDigit(character) ||
                        character == '_')))
            {
                throw new ArgumentException(
                    "LLM credential bindings require valid provider, profile, " +
                    "and environment-variable names.",
                    nameof(bindings));
            }

            if (!resolved.TryAdd(
                    (binding.ProviderName, binding.CredentialProfile),
                    binding.EnvironmentVariable))
            {
                throw new ArgumentException(
                    "LLM credential bindings must be unique.",
                    nameof(bindings));
            }
        }

        environmentVariables = resolved;
        this.readEnvironmentVariable =
            readEnvironmentVariable ??
            Environment.GetEnvironmentVariable;
    }

    public Task<string> GetApiKeyAsync(
        string providerName,
        string credentialProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!environmentVariables.TryGetValue(
                (providerName, credentialProfile),
                out string? environmentVariable))
        {
            throw new InvalidOperationException(
                $"LLM credential profile '{credentialProfile}' is not " +
                $"registered for provider '{providerName}'.");
        }

        string? value = readEnvironmentVariable(environmentVariable);
        return Task.FromResult(
            !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException(
                    $"Environment variable '{environmentVariable}' for LLM " +
                    "credential profile is unset or empty."));
    }

    private sealed class CredentialBindingComparer
        : IEqualityComparer<(string Provider, string Profile)>
    {
        internal static readonly CredentialBindingComparer Instance = new();

        public bool Equals(
            (string Provider, string Profile) x,
            (string Provider, string Profile) y) =>
            string.Equals(
                x.Provider,
                y.Provider,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                x.Profile,
                y.Profile,
                StringComparison.Ordinal);

        public int GetHashCode(
            (string Provider, string Profile) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Provider),
                StringComparer.Ordinal.GetHashCode(value.Profile));
    }
}
