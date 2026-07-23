using AkosFabric.Domain.RepositorySessions;

namespace AkosFabric.UnitTests.RepositorySessions;

public sealed class RepositorySessionTests
{
    [Fact]
    public void TransitionToAllowedTransitionUpdatesStatus()
    {
        var session = new RepositorySession(
            Guid.NewGuid(),
            "akos-fabric",
            Guid.NewGuid());

        session.TransitionTo(RepositorySessionStatus.Published);

        Assert.Equal(RepositorySessionStatus.Published, session.Status);
    }

    [Fact]
    public void ConfirmedDeliveryCanStartFromCreatedAfterPublishUncertainty()
    {
        var session = new RepositorySession(
            Guid.NewGuid(),
            "akos-fabric",
            Guid.NewGuid());

        session.TransitionTo(RepositorySessionStatus.Starting);

        Assert.Equal(RepositorySessionStatus.Starting, session.Status);
    }

    [Fact]
    public void TransitionToTerminalSessionThrows()
    {
        var session = new RepositorySession(
            Guid.NewGuid(),
            "akos-fabric",
            Guid.NewGuid());
        session.TransitionTo(RepositorySessionStatus.Failed);

        var exception = Assert.Throws<InvalidOperationException>(
            () => session.TransitionTo(RepositorySessionStatus.Starting));

        Assert.Contains("cannot transition", exception.Message, StringComparison.Ordinal);
    }
}
