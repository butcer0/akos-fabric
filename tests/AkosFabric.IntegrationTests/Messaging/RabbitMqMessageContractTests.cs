using System.Text.Json;

using AkosFabric.Application.Messaging;
using AkosFabric.Infrastructure.Messaging;

namespace AkosFabric.IntegrationTests.Messaging;

public sealed class RabbitMqMessageContractTests
{
    [Fact]
    public void SerializesExactMetadataOnlyVersionOneContract()
    {
        var message = new RepositorySessionRequestedV1(
            SchemaVersion: 1,
            MessageId: Guid.Parse("3631ff74-1391-4518-b6b0-ed04819ee346"),
            RepositorySessionId:
                Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c"),
            TraceParent:
                "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01",
            RequestedAt:
                DateTimeOffset.Parse(
                    "2026-07-23T12:30:00Z",
                    System.Globalization.CultureInfo.InvariantCulture));

        using var payload = JsonDocument.Parse(
            RabbitMqRepositorySessionQueue.Serialize(message));
        var properties = payload.RootElement.EnumerateObject().ToArray();

        Assert.Equal(
            [
                "schemaVersion",
                "messageId",
                "repositorySessionId",
                "traceparent",
                "requestedAt",
            ],
            properties.Select(property => property.Name));
        Assert.Equal(1, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            message.MessageId,
            payload.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(
            message.RepositorySessionId,
            payload.RootElement.GetProperty("repositorySessionId").GetGuid());
        Assert.Equal(
            message.TraceParent,
            payload.RootElement.GetProperty("traceparent").GetString());

        var json = payload.RootElement.GetRawText();
        Assert.DoesNotContain("jira", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceCode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictDeserializerAcceptsOnlyExactMetadataContract()
    {
        var message = new RepositorySessionRequestedV1(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01",
            new DateTimeOffset(
                2026,
                7,
                23,
                12,
                30,
                0,
                TimeSpan.Zero));

        RepositorySessionRequestedV1 deserialized =
            RepositorySessionMessageCodec.Deserialize(
                RabbitMqRepositorySessionQueue.Serialize(message));

        Assert.Equal(message, deserialized);
    }

    [Theory]
    [InlineData(
        """{"schemaVersion":2,"messageId":"3631ff74-1391-4518-b6b0-ed04819ee346","repositorySessionId":"6a92a62a-1e93-4b5b-a52c-dcc541fb591c","traceparent":"00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01","requestedAt":"2026-07-23T12:30:00Z"}""")]
    [InlineData(
        """{"schemaVersion":1,"messageId":"3631ff74-1391-4518-b6b0-ed04819ee346","repositorySessionId":"6a92a62a-1e93-4b5b-a52c-dcc541fb591c","traceParent":"00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01","requestedAt":"2026-07-23T12:30:00Z"}""")]
    [InlineData(
        """{"schemaVersion":1,"messageId":"3631ff74-1391-4518-b6b0-ed04819ee346","repositorySessionId":"6a92a62a-1e93-4b5b-a52c-dcc541fb591c","traceparent":"invalid","requestedAt":"2026-07-23T12:30:00Z"}""")]
    [InlineData(
        """{"schemaVersion":1,"messageId":"3631ff74-1391-4518-b6b0-ed04819ee346","repositorySessionId":"6a92a62a-1e93-4b5b-a52c-dcc541fb591c","traceparent":"00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01","requestedAt":"2026-07-23T12:30:00Z","jira":{"body":"forbidden"}}""")]
    public void StrictDeserializerRejectsSchemaDriftOrNonMetadataFields(
        string json)
    {
        Assert.Throws<RepositorySessionMessageFormatException>(
            () => RepositorySessionMessageCodec.Deserialize(
                System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void RejectsNonAmqpConnectionScheme()
    {
        var options = new RabbitMqOptions
        {
            ConnectionUri = new Uri("https://rabbitmq.invalid"),
        };

        Assert.Throws<ArgumentException>(
            () => new RabbitMqRepositorySessionQueue(options));
    }
}
