namespace AkosFabric.Application.RepositoryProfiles.Models;

public sealed class RepositoryProfileException : Exception
{
    public RepositoryProfileException(string message)
        : base(message)
    {
    }

    public RepositoryProfileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
