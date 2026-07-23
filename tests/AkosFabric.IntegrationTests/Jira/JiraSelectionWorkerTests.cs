using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Infrastructure.Jira;

using Microsoft.Extensions.Logging.Abstractions;

namespace AkosFabric.IntegrationTests.Jira;

public sealed class JiraSelectionWorkerTests
{
    [Fact]
    public void OptionsUseSpecifiedDefaultIntervalAndRejectInvalidValues()
    {
        Assert.Equal(300, new JiraSelectionOptions().PollingIntervalSeconds);

        Assert.Throws<InvalidOperationException>(
            () => new JiraSelectionOptions
            {
                PollingIntervalSeconds = 0,
            }.Validate());
        Assert.Throws<InvalidOperationException>(
            () => new JiraSelectionOptions
            {
                EnabledRepositoryProfiles =
                    ["akos-fabric", "AKOS-FABRIC"],
            }.Validate());
    }

    [Fact]
    public async Task PollFailureIsRetriedOnNextIntervalWithoutOverlap()
    {
        var service = new RetrySelectionService();
        using var worker = new JiraSelectionWorker(
            new JiraSelectionOptions
            {
                PollingIntervalSeconds = 1,
                EnabledRepositoryProfiles = ["akos-fabric"],
            },
            service,
            NullLogger<JiraSelectionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await service.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(service.CallCount >= 2);
        Assert.Equal(1, service.MaximumConcurrency);
        Assert.All(
            service.ProfileArguments,
            profiles => Assert.Equal(["akos-fabric"], profiles));
    }

    private sealed class RetrySelectionService : IJiraSelectionService
    {
        private int activeCalls;
        private int callCount;
        private int maximumConcurrency;

        public TaskCompletionSource SecondCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref callCount);
        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public List<IReadOnlyList<string>> ProfileArguments { get; } = [];

        public Task<JiraSelectionResult> SelectAsync(
            IReadOnlyList<string> enabledRepositoryProfiles,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref activeCalls);
            InterlockedExtensions.Max(ref maximumConcurrency, active);
            int call = Interlocked.Increment(ref callCount);
            lock (ProfileArguments)
            {
                ProfileArguments.Add(enabledRepositoryProfiles.ToArray());
            }

            try
            {
                if (call == 1)
                {
                    throw new IOException("simulated Jira query outage");
                }

                SecondCall.TrySetResult();
                return Task.FromResult(
                    new JiraSelectionResult(
                        JiraSelectionOutcome.NoCandidates,
                        1,
                        0,
                        null,
                        null));
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            int current = Volatile.Read(ref location);
            while (current < value)
            {
                int observed = Interlocked.CompareExchange(
                    ref location,
                    value,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
