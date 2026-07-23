using System.Diagnostics.CodeAnalysis;

using AkosFabric.Application.Messaging;

namespace AkosFabric.Application.RepositorySessions.Interfaces;

[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The architecture specification names this transport abstraction IRepositorySessionQueue.")]
public interface IRepositorySessionQueue
{
    Task PublishAsync(
        RepositorySessionRequestedV1 message,
        CancellationToken cancellationToken);
}
