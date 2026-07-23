using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Application.SourceControl.Interfaces;

public interface ISourceControlCredentialAcquisitionService
{
    Task<SourceControlCredential> AcquireForSessionAsync(
        RepositorySessionRecord session,
        RepositoryProfile profile,
        CancellationToken cancellationToken);
}
