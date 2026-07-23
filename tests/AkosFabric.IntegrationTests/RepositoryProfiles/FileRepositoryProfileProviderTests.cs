using System.Diagnostics;
using System.Text.RegularExpressions;

using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Infrastructure.RepositoryProfiles;

namespace AkosFabric.IntegrationTests.RepositoryProfiles;

public sealed class FileRepositoryProfileProviderTests
{
    [Fact]
    public async Task LoadsValidatedProfileAndRecordsCheckoutRevision()
    {
        var checkout = await CreateProfileCheckoutAsync(validDigest: true);
        try
        {
            var provider = CreateProvider(checkout);

            var profile = await provider.FindAsync(
                "akos-fabric",
                CancellationToken.None);

            Assert.NotNull(profile);
            Assert.Equal("akos-fabric", profile.Id);
            Assert.Equal("KAN", profile.Jira.ProjectKey);
            Assert.Equal("default", profile.Jira.Site);
            Assert.Equal("github", profile.SourceControl.Provider);
            Assert.Equal("main", profile.Repository.DefaultBranch);
            Assert.Empty(profile.SupplementalRepositories);
            Assert.Equal("gemini-3.6-flash", profile.Llm.ModelId);
            Assert.Matches("^[0-9a-f]{40}$", profile.ProfileRevisionSha);
        }
        finally
        {
            DeleteCheckout(checkout);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "    writable: true")]
    public async Task LoadsTypedSupplementalRepository(
        bool expectedWritable,
        string? writableProperty)
    {
        var supplementalRepositories = string.Join(
            Environment.NewLine,
            "supplementalRepositories:",
            "  - providerRepositoryId: example/supporting-repository",
            "    cloneUrl: https://github.com/example/supporting-repository.git",
            "    defaultBranch: main",
            "    cloneStrategy: full",
            "    gitLfs: false",
            "    submodules: none",
            writableProperty);
        var checkout = await CreateProfileCheckoutAsync(
            validDigest: true,
            supplementalRepositories);
        try
        {
            var provider = CreateProvider(checkout);

            var profile = await provider.FindAsync(
                "akos-fabric",
                CancellationToken.None);

            var supplemental = Assert.Single(
                Assert.IsType<RepositoryProfile>(profile)
                    .SupplementalRepositories);
            Assert.Equal(
                "example/supporting-repository",
                supplemental.ProviderRepositoryId);
            Assert.Equal(
                new Uri(
                    "https://github.com/example/supporting-repository.git"),
                supplemental.CloneUrl);
            Assert.Equal(expectedWritable, supplemental.Writable);
        }
        finally
        {
            DeleteCheckout(checkout);
        }
    }

    [Fact]
    public async Task RejectsUnknownSupplementalRepositoryProperty()
    {
        var supplementalRepositories = string.Join(
            Environment.NewLine,
            "supplementalRepositories:",
            "  - providerRepositoryId: example/supporting-repository",
            "    cloneUrl: https://github.com/example/supporting-repository.git",
            "    defaultBranch: main",
            "    cloneStrategy: full",
            "    gitLfs: false",
            "    submodules: none",
            "    credential: must-not-be-in-profile");
        var checkout = await CreateProfileCheckoutAsync(
            validDigest: true,
            supplementalRepositories);
        try
        {
            var provider = CreateProvider(checkout);

            var exception = await Assert.ThrowsAsync<RepositoryProfileException>(
                () => provider.FindAsync(
                    "akos-fabric",
                    CancellationToken.None));

            Assert.Contains(
                "/supplementalRepositories/0",
                exception.Message);
            Assert.Contains("additionalProperties", exception.Message);
        }
        finally
        {
            DeleteCheckout(checkout);
        }
    }

    [Fact]
    public async Task ConstructsProvidersInParallelWithIsolatedSchemaRegistries()
    {
        const int providerCount = 16;
        var checkout = await CreateProfileCheckoutAsync(validDigest: true);
        var schemaDirectory = Path.Combine(
            Path.GetTempPath(),
            $"akos-profile-schemas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(schemaDirectory);
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var schema = await File.ReadAllTextAsync(
                Path.Combine(
                    repositoryRoot,
                    "schemas",
                    "repository-profile-v1.schema.json"),
                CancellationToken.None);
            var schemaPaths = Enumerable.Range(0, providerCount)
                .Select(index => Path.Combine(
                    schemaDirectory,
                    $"repository-profile-{index}.schema.json"))
                .ToArray();
            await Task.WhenAll(schemaPaths.Select(
                path => File.WriteAllTextAsync(
                    path,
                    schema,
                    CancellationToken.None)));

            var providers = await Task.WhenAll(schemaPaths.Select(
                path => Task.Run(
                    () => CreateProvider(checkout, path))));

            Assert.Equal(providerCount, providers.Length);
        }
        finally
        {
            DeleteCheckout(checkout);
            Directory.Delete(schemaDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsProfileThatDoesNotConformToSchema()
    {
        var checkout = await CreateProfileCheckoutAsync(validDigest: false);
        try
        {
            var provider = CreateProvider(checkout);

            var exception = await Assert.ThrowsAsync<RepositoryProfileException>(
                () => provider.FindAsync(
                    "akos-fabric",
                    CancellationToken.None));

            Assert.Contains("does not conform", exception.Message);
            Assert.Contains("/image/expectedDigest", exception.Message);
            Assert.Contains("pattern", exception.Message);
        }
        finally
        {
            DeleteCheckout(checkout);
        }
    }

    [Theory]
    [InlineData("../akos-fabric")]
    [InlineData("AkosFabric")]
    [InlineData("akos_fabric")]
    public async Task RejectsUnsafeProfileNames(string profileName)
    {
        var checkout = await CreateProfileCheckoutAsync(validDigest: true);
        try
        {
            var provider = CreateProvider(checkout);

            await Assert.ThrowsAsync<RepositoryProfileException>(
                () => provider.FindAsync(
                    profileName,
                    CancellationToken.None));
        }
        finally
        {
            DeleteCheckout(checkout);
        }
    }

    private static FileRepositoryProfileProvider CreateProvider(
        string checkout,
        string? schemaPath = null)
    {
        var repositoryRoot = FindRepositoryRoot();
        return new FileRepositoryProfileProvider(
            new RepositoryProfileOptions
            {
                RootPath = checkout,
                SchemaPath = schemaPath ?? Path.Combine(
                    repositoryRoot,
                    "schemas",
                    "repository-profile-v1.schema.json"),
            });
    }

    private static async Task<string> CreateProfileCheckoutAsync(
        bool validDigest,
        string? supplementalRepositories = null)
    {
        var repositoryRoot = FindRepositoryRoot();
        var checkout = Path.Combine(
            Path.GetTempPath(),
            $"akos-profile-tests-{Guid.NewGuid():N}");
        var profiles = Path.Combine(checkout, "profiles");
        Directory.CreateDirectory(profiles);

        var template = await File.ReadAllTextAsync(
            Path.Combine(
                repositoryRoot,
                "repository-profiles",
                "profiles",
                "akos-fabric.yml"),
            CancellationToken.None);
        var digestPattern = new Regex(
            @"(?m)^  expectedDigest: .+$",
            RegexOptions.CultureInvariant);
        template = digestPattern.Replace(
            template,
            validDigest
                ? $"  expectedDigest: sha256:{new string('a', 64)}"
                : "  expectedDigest: invalid",
            count: 1);

        if (supplementalRepositories is not null)
        {
            template = template.Replace(
                "supplementalRepositories: []",
                supplementalRepositories,
                StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(
            Path.Combine(profiles, "akos-fabric.yml"),
            template,
            CancellationToken.None);

        RunGit(checkout, "init");
        RunGit(checkout, "config", "user.name", "Akos Fabric Tests");
        RunGit(checkout, "config", "user.email", "tests@akos-fabric.invalid");
        RunGit(checkout, "add", "profiles/akos-fabric.yml");
        RunGit(checkout, "commit", "-m", "Add test repository profile");
        return checkout;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start.");
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {standardError}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "schemas",
                        "repository-profile-v1.schema.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Cannot locate the Akos Fabric repository root.");
    }

    private static void DeleteCheckout(string checkout)
    {
        if (!Directory.Exists(checkout))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(
                     checkout,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(checkout, recursive: true);
    }
}
