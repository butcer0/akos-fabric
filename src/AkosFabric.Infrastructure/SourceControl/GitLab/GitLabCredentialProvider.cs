using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Infrastructure.SourceControl.GitLab;

public sealed class GitLabCredentialProvider
    : ISourceControlCredentialProvider
{
    public string ProviderName => "gitlab";

    public Task<SourceControlCredential> GetCredentialAsync(
        SourceRepositoryReference repository,
        SourceControlPermissionSet permissions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "GitLab credential delivery is intentionally not implemented in version one.");
}
