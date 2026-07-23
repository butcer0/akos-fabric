using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Application.SourceControl.Services;

public sealed class SourceControlCredentialAcquisitionService
    : ISourceControlCredentialAcquisitionService
{
    private static readonly SourceControlPermissionSet SessionPermissions =
        new(
            CanReadRepository: true,
            CanPushBranch: true,
            CanCreateChangeRequest: false);

    private readonly ISourceControlCredentialProviderResolver providers;
    private readonly TimeProvider timeProvider;

    public SourceControlCredentialAcquisitionService(
        ISourceControlCredentialProviderResolver providers,
        TimeProvider timeProvider)
    {
        this.providers =
            providers ?? throw new ArgumentNullException(nameof(providers));
        this.timeProvider =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<SourceControlCredential> AcquireForSessionAsync(
        RepositorySessionRecord session,
        RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profile);
        ValidateIdentity(session, profile);

        ISourceControlCredentialProvider provider = providers.Resolve(
            profile.SourceControl.Provider,
            profile.SourceControl.AuthenticationProfile);
        SourceControlCredential credential =
            await provider.GetCredentialAsync(
                new SourceRepositoryReference(
                    profile.SourceControl.Provider,
                    profile.Repository.ProviderRepositoryId,
                    profile.Repository.CloneUrl),
                SessionPermissions,
                cancellationToken);
        ValidateCredential(credential);
        return credential;
    }

    private static void ValidateIdentity(
        RepositorySessionRecord session,
        RepositoryProfile profile)
    {
        if (!string.Equals(
                session.RepositoryProfile,
                profile.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                session.ProfileRevisionSha,
                profile.ProfileRevisionSha,
                StringComparison.Ordinal) ||
            !string.Equals(
                session.SourceControlProvider,
                profile.SourceControl.Provider,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The repository profile identity does not match the durable session.");
        }
    }

    private void ValidateCredential(SourceControlCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.Username) ||
            string.IsNullOrEmpty(credential.Secret) ||
            credential.ExpiresAt <= timeProvider.GetUtcNow())
        {
            throw new InvalidDataException(
                "The source-control provider returned an invalid or expired credential.");
        }
    }
}
