using System.Text.Json.Serialization;

namespace AkosFabric.Application.Messaging;

public sealed record RepositorySessionRequestedV1(
    int SchemaVersion,
    Guid MessageId,
    Guid RepositorySessionId,
    [property: JsonPropertyName("traceparent")]
    string TraceParent,
    DateTimeOffset RequestedAt);
