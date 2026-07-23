using AkosFabric.Application.RepositoryProfiles.Models;

namespace AkosFabric.Application.RepositoryProfiles.Interfaces;

public interface IRepositoryProfileProvider
{
    Task<RepositoryProfile?> FindAsync(string profileName, CancellationToken cancellationToken);
}
