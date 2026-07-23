using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

using AkosFabric.Application.Messaging;

namespace AkosFabric.Infrastructure.Messaging;

internal static class RepositorySessionMessageCodec
{
    public const string ContentType = "application/json";
    public const string ContentEncoding = "utf-8";
    public const string MessageType = "repository-session.v1";

    private static readonly FrozenSet<string> RequiredProperties =
        new[]
        {
            "schemaVersion",
            "messageId",
            "repositorySessionId",
            "traceparent",
            "requestedAt",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static RepositorySessionRequestedV1 Deserialize(
        ReadOnlyMemory<byte> body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RepositorySessionMessageFormatException(
                    "Repository-session message body must be a JSON object.");
            }

            string[] propertyNames = document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray();
            if (propertyNames.Length != RequiredProperties.Count ||
                propertyNames.Distinct(StringComparer.Ordinal).Count() !=
                    propertyNames.Length ||
                propertyNames.Any(name => !RequiredProperties.Contains(name)))
            {
                throw new RepositorySessionMessageFormatException(
                    "Repository-session message must contain exactly the schema-v1 metadata properties.");
            }

            RepositorySessionRequestedV1? message =
                document.RootElement.Deserialize<RepositorySessionRequestedV1>(
                    DeserializerOptions);
            Validate(message);
            return message!;
        }
        catch (RepositorySessionMessageFormatException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or FormatException)
        {
            throw new RepositorySessionMessageFormatException(
                "Repository-session message is not valid schema-v1 JSON.",
                exception);
        }
    }

    public static void Validate(RepositorySessionRequestedV1? message)
    {
        if (message is null)
        {
            throw new RepositorySessionMessageFormatException(
                "Repository-session message cannot be null.");
        }

        if (message.SchemaVersion != 1)
        {
            throw new RepositorySessionMessageFormatException(
                "Only repository-session message schema version 1 is supported.");
        }

        if (message.MessageId == Guid.Empty ||
            message.RepositorySessionId == Guid.Empty)
        {
            throw new RepositorySessionMessageFormatException(
                "Message and repository-session IDs must be non-empty.");
        }

        if (!ActivityContext.TryParse(
                message.TraceParent,
                traceState: null,
                out _))
        {
            throw new RepositorySessionMessageFormatException(
                "A valid W3C traceparent is required.");
        }

        if (message.RequestedAt == default ||
            message.RequestedAt.Offset != TimeSpan.Zero)
        {
            throw new RepositorySessionMessageFormatException(
                "requestedAt must be a UTC timestamp.");
        }
    }
}
