using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Application.SourceControl.Interfaces;

public interface ISourceControlCredentialProvider
{
    string ProviderName { get; }

    Task<SourceControlCredential> GetCredentialAsync(
        SourceRepositoryReference repository,
        SourceControlPermissionSet permissions,
        CancellationToken cancellationToken);
}
