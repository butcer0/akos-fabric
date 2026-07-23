using System.Reflection;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.UnitTests.SourceControl;

public sealed class SourceControlVocabularyTests
{
    [Fact]
    public void ApplicationSourceControlContractUsesNeutralChangeRequestVocabulary()
    {
        var publicTypeNames = typeof(ISourceControlProvider).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith(
                "AkosFabric.Application.SourceControl",
                StringComparison.Ordinal) is true)
            .Select(type => type.Name)
            .ToArray();

        Assert.Contains(nameof(ChangeRequestReference), publicTypeNames);
        Assert.Contains(nameof(CreateChangeRequest), publicTypeNames);
        Assert.DoesNotContain(publicTypeNames, name =>
            name.Contains("PullRequest", StringComparison.Ordinal)
            || name.Contains("MergeRequest", StringComparison.Ordinal));
    }
}
