using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.Messaging;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.Messaging;

using RabbitMQ.Client;

namespace AkosFabric.IntegrationTests.Messaging;

public sealed class RabbitMqConsumerLiveTests
{
    [Fact]
    [Trait("Dependency", "RabbitMQ")]
    public async Task FailedDeliveryIsRedeliveredThenManuallyAcknowledged()
    {
        string? connectionUri = Environment.GetEnvironmentVariable(
            "AKOS_RABBITMQ_TEST_URI");
        if (string.IsNullOrWhiteSpace(connectionUri))
        {
            return;
        }

        var options = new RabbitMqOptions
        {
            ConnectionUri = new Uri(connectionUri),
            ConfirmationTimeout = TimeSpan.FromSeconds(10),
        };
        await PurgeAsync(options);

        var repository = new FailOnceRepository();
        var handler = new RepositorySessionDeliveryHandler(
            repository,
            new UnusedExecutor(),
            new UnusedRequestFactory(),
            new UnusedMonitor());
        var consumer = new RabbitMqRepositorySessionConsumer(
            options,
            handler);
        await consumer.StartAsync(CancellationToken.None);

        try
        {
            var message = new RepositorySessionRequestedV1(
                1,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "00-44e67a7b41c4fcddf926a495f86d19cc-4d916c73cb40d1be-01",
                DateTimeOffset.UtcNow);
            var publisher = new RabbitMqRepositorySessionQueue(options);
            await publisher.PublishAsync(
                message,
                CancellationToken.None);

            await repository.SecondDelivery.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                CancellationToken.None);
        }
        finally
        {
            using var stopTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await consumer.StopAsync(stopTimeout.Token);
            consumer.Dispose();
        }

        Assert.True(repository.Attempts >= 2);
        Assert.Null(await GetAsync(options));
    }

    private static async Task PurgeAsync(RabbitMqOptions options)
    {
        var factory = new ConnectionFactory
        {
            Uri = options.ConnectionUri,
        };
        await using IConnection connection =
            await factory.CreateConnectionAsync(CancellationToken.None);
        await using IChannel channel =
            await connection.CreateChannelAsync(
                cancellationToken: CancellationToken.None);
        await RabbitMqTopology.DeclareAsync(
            channel,
            options,
            CancellationToken.None);
        await channel.QueuePurgeAsync(
            options.Queue,
            CancellationToken.None);
    }

    private static async Task<BasicGetResult?> GetAsync(
        RabbitMqOptions options)
    {
        var factory = new ConnectionFactory
        {
            Uri = options.ConnectionUri,
        };
        await using IConnection connection =
            await factory.CreateConnectionAsync(CancellationToken.None);
        await using IChannel channel =
            await connection.CreateChannelAsync(
                cancellationToken: CancellationToken.None);
        return await channel.BasicGetAsync(
            options.Queue,
            autoAck: false,
            CancellationToken.None);
    }

    private sealed class FailOnceRepository
        : IRepositorySessionRepository
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        public TaskCompletionSource SecondDelivery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RepositorySessionRecord?> FindAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref attempts);
            if (current == 1)
            {
                throw new InvalidOperationException(
                    "Synthetic transient repository failure.");
            }

            SecondDelivery.TrySetResult();
            return Task.FromResult<RepositorySessionRecord?>(null);
        }

        public Task<IReadOnlyList<WorkItemRunRecord>> ListWorkItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionRecord>> ListRecoverableAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateAsync(
            RepositorySessionCreation session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            RepositorySessionCancellation cancellation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionSessionStatusAsync(
            RepositorySessionStatusTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionWorkItemStatusAsync(
            WorkItemRunStatusTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordValidatedResultAsync(
            AgentResultRecording result,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordChangeRequestAsync(
            ChangeRequestRecording recording,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordJiraSynchronizationWarningAsync(
            JiraSynchronizationWarningRecording recording,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailResultProcessingAsync(
            AgentResultProcessingFailure failure,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteResultProcessingAsync(
            AgentResultProcessingCompletion completion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedExecutor : IRepositorySessionExecutor
    {
        public Task<RepositorySessionExecution> StartAsync(
            RepositorySessionExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionContainer?> InspectAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionWaitResult> WaitAsync(
            Guid repositorySessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StreamLogsAsync(
            Guid repositorySessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplaceCredentialAsync(
            Guid repositorySessionId,
            SourceControlCredential credential,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteCredentialAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StopAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySessionContainer>> ListManagedAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedRequestFactory
        : IRepositorySessionExecutionRequestFactory
    {
        public Task<RepositorySessionExecutionRequest> CreateAsync(
            RepositorySessionRecord session,
            RepositorySessionRequestedV1 message,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedMonitor
        : IRepositorySessionMonitorAttacher
    {
        public Task AttachAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
