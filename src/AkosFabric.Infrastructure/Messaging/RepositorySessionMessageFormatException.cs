namespace AkosFabric.Infrastructure.Messaging;

public sealed class RepositorySessionMessageFormatException : Exception
{
    public RepositorySessionMessageFormatException(string message)
        : base(message)
    {
    }

    public RepositorySessionMessageFormatException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
