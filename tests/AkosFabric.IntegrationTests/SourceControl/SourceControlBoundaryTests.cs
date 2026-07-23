using System.Reflection;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.IntegrationTests.SourceControl;

public sealed class SourceControlBoundaryTests
{
    [Fact]
    public void ApplicationSourceControlContractContainsNoProviderVocabulary()
    {
        Assembly applicationAssembly =
            typeof(ISourceControlProvider).Assembly;
        Type[] sourceControlTypes = applicationAssembly
            .GetTypes()
            .Where(type =>
                type.Namespace?.Contains(
                    ".SourceControl",
                    StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(sourceControlTypes);
        Assert.All(
            sourceControlTypes,
            type =>
            {
                Assert.DoesNotContain(
                    "GitHub",
                    type.FullName!,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "GitLab",
                    type.FullName!,
                    StringComparison.OrdinalIgnoreCase);
            });

        ConstructorInfo constructor =
            Assert.Single(typeof(ChangeRequestReference).GetConstructors());
        Assert.Equal(
            [
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(Uri),
                typeof(string),
            ],
            constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
    }
}
