using AkosFabric.Application.SourceControl.Interfaces;

namespace AkosFabric.Infrastructure.SourceControl;

public sealed record SourceControlCredentialProviderRegistration(
    string ProviderName,
    string AuthenticationProfile,
    ISourceControlCredentialProvider Provider);

public sealed class SourceControlCredentialProviderResolver
    : ISourceControlCredentialProviderResolver
{
    private readonly Dictionary<
        (string Provider, string Profile),
        ISourceControlCredentialProvider> providers;

    public SourceControlCredentialProviderResolver(
        IEnumerable<SourceControlCredentialProviderRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var resolved = new Dictionary<
            (string Provider, string Profile),
            ISourceControlCredentialProvider>(
            CredentialBindingComparer.Instance);
        foreach (SourceControlCredentialProviderRegistration registration
                 in registrations)
        {
            if (string.IsNullOrWhiteSpace(registration.ProviderName) ||
                string.IsNullOrWhiteSpace(registration.AuthenticationProfile))
            {
                throw new ArgumentException(
                    "Credential provider registrations require a provider " +
                    "name and authentication profile.",
                    nameof(registrations));
            }

            ArgumentNullException.ThrowIfNull(registration.Provider);
            if (!string.Equals(
                    registration.ProviderName,
                    registration.Provider.ProviderName,
                    StringComparison.OrdinalIgnoreCase) ||
                !resolved.TryAdd(
                    (
                        registration.ProviderName,
                        registration.AuthenticationProfile
                    ),
                    registration.Provider))
            {
                throw new ArgumentException(
                    "Credential provider registrations must be unique and " +
                    "match the provider implementation.",
                    nameof(registrations));
            }
        }

        providers = resolved;
    }

    public ISourceControlCredentialProvider Resolve(
        string providerName,
        string authenticationProfile)
    {
        if (string.IsNullOrWhiteSpace(providerName) ||
            string.IsNullOrWhiteSpace(authenticationProfile))
        {
            throw new ArgumentException(
                "A provider name and authentication profile are required.");
        }

        return providers.TryGetValue(
            (providerName, authenticationProfile),
            out ISourceControlCredentialProvider? provider)
            ? provider
            : throw new InvalidOperationException(
                $"Source-control credential profile " +
                $"'{authenticationProfile}' is not registered for provider " +
                $"'{providerName}'.");
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
