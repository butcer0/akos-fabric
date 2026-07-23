namespace AkosFabric.Application.Common.Exceptions;

public class RepositorySessionException : Exception
{
    public RepositorySessionException(string message)
        : base(message)
    {
    }

    public RepositorySessionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RepositorySessionNotFoundException : RepositorySessionException
{
    public RepositorySessionNotFoundException(Guid id)
        : base($"Repository session '{id}' was not found.")
    {
    }
}

public sealed class RepositorySessionValidationException : RepositorySessionException
{
    public RepositorySessionValidationException(string message)
        : base(message)
    {
    }
}

public sealed class RepositorySessionConflictException : RepositorySessionException
{
    public RepositorySessionConflictException(string message)
        : base(message)
    {
    }

    public RepositorySessionConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RepositorySessionPublicationException : RepositorySessionException
{
    public RepositorySessionPublicationException(
        Guid repositorySessionId,
        Exception innerException)
        : base(
            $"Repository session '{repositorySessionId}' was created but could not be published.",
            innerException)
    {
        RepositorySessionId = repositorySessionId;
    }

    public Guid RepositorySessionId { get; }
}

public sealed class RepositorySessionSynchronizationException : RepositorySessionException
{
    public RepositorySessionSynchronizationException(string message)
        : base(message)
    {
    }
}
