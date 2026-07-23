namespace AkosFabric.IntegrationTests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void IdentityDoesNotReferenceAgentControlAssemblies()
    {
        var referencedAssemblyNames = typeof(AkosFabric.Identity.IdentityAssembly).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain(referencedAssemblyNames, name =>
            name?.StartsWith("AkosFabric.", StringComparison.Ordinal) is true);
    }
}
