using AkosFabric.Domain.WorkItems;

namespace AkosFabric.UnitTests.WorkItems;

public sealed class WorkItemRunTests
{
    [Fact]
    public void ConstructorNonPositiveSequenceNumberThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkItemRun(Guid.NewGuid(), 0, "10001", "KAN-1"));
    }

    [Fact]
    public void TransitionToRevisionCycleReturnsToVerification()
    {
        var workItem = new WorkItemRun(Guid.NewGuid(), 1, "10001", "KAN-1");
        workItem.TransitionTo(WorkItemRunStatus.Planning);
        workItem.TransitionTo(WorkItemRunStatus.Coding);
        workItem.TransitionTo(WorkItemRunStatus.Verifying);
        workItem.TransitionTo(WorkItemRunStatus.Judging);
        workItem.TransitionTo(WorkItemRunStatus.Revising);

        workItem.TransitionTo(WorkItemRunStatus.Verifying);

        Assert.Equal(WorkItemRunStatus.Verifying, workItem.Status);
    }
}
