using System.Collections.Frozen;
using AkosFabric.Application.SourceControl.Interfaces;

namespace AkosFabric.Infrastructure.SourceControl;

public sealed class SourceControlProviderResolver : ISourceControlProviderResolver
{
    private readonly FrozenDictionary<string, ISourceControlProvider> _providers;

    public SourceControlProviderResolver(IEnumerable<ISourceControlProvider> providers)
    {
        _providers = providers.ToFrozenDictionary(
            provider => provider.ProviderName,
            StringComparer.OrdinalIgnoreCase);
    }

    public ISourceControlProvider Resolve(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException(
                "A source-control provider name is required.",
                nameof(providerName));
        }

        return _providers.TryGetValue(providerName, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"Source-control provider '{providerName}' is not registered.");
    }
}
