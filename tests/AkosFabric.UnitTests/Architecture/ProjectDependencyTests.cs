using System.Xml.Linq;

namespace AkosFabric.UnitTests.Architecture;

public sealed class ProjectDependencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("AkosFabric.Domain")]
    [InlineData("AkosFabric.Identity")]
    public void IndependentProjectsHaveNoSolutionProjectReferences(string projectName)
    {
        Assert.Empty(ReadProjectReferences(projectName));
    }

    [Fact]
    public void ApplicationReferencesOnlyDomain()
    {
        Assert.Equal(
            ["AkosFabric.Domain"],
            ReadProjectReferences("AkosFabric.Application"));
    }

    [Fact]
    public void InfrastructureReferencesOnlyApplicationAndDomain()
    {
        Assert.Equal(
            ["AkosFabric.Application", "AkosFabric.Domain"],
            ReadProjectReferences("AkosFabric.Infrastructure"));
    }

    [Fact]
    public void ApiReferencesOnlyApplicationAndInfrastructure()
    {
        Assert.Equal(
            ["AkosFabric.Application", "AkosFabric.Infrastructure"],
            ReadProjectReferences("AkosFabric.Api"));
    }

    private static string[] ReadProjectReferences(string projectName)
    {
        var projectPath = Path.Combine(
            RepositoryRoot,
            "src",
            projectName,
            $"{projectName}.csproj");
        var document = XDocument.Load(projectPath);

        return document
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(path => path is not null)
            .Select(path => Path.GetFileNameWithoutExtension(
                path!.Replace(
                    Path.AltDirectorySeparatorChar,
                    Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AkosFabric.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root containing AkosFabric.slnx.");
    }
}
