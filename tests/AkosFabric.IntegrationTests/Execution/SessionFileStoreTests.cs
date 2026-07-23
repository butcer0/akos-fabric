using System.Globalization;
using System.Text;
using System.Text.Json;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.IntegrationTests.Execution;

public sealed class SessionFileStoreTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "akos-session-file-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WritesContractFilesAtomicallyAndReplacesCredential()
    {
        var sessionId = Guid.NewGuid();
        var store = CreateStore();
        var manifest = Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":1,"repositorySessionId":"{{sessionId:D}}"}""");

        await store.WriteManifestAsync(sessionId, manifest, CancellationToken.None);
        var result = Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"status":"completed","workItems":[]}""");
        await store.WriteResultAsync(sessionId, result, CancellationToken.None);
        await store.InitializeLogFilesAsync(sessionId, CancellationToken.None);
        await store.ReplaceCredentialAsync(
            sessionId,
            new SourceControlCredential(
                "x-access-token",
                "first-secret",
                DateTimeOffset.Parse("2026-07-23T13:30:00Z", CultureInfo.InvariantCulture)),
            CancellationToken.None);
        await store.ReplaceCredentialAsync(
            sessionId,
            new SourceControlCredential(
                "x-access-token",
                "replacement-secret",
                DateTimeOffset.Parse("2026-07-23T14:30:00Z", CultureInfo.InvariantCulture)),
            CancellationToken.None);

        var sessionDirectory = store.GetSessionDirectory(sessionId);
        Assert.Equal(
            Path.Combine(rootDirectory, sessionId.ToString("D")),
            sessionDirectory);
        Assert.Equal(
            manifest,
            await File.ReadAllBytesAsync(
                store.GetSessionFile(sessionId, SessionFileStore.ManifestFileName)));
        Assert.Equal(
            result,
            await File.ReadAllBytesAsync(
                store.GetSessionFile(sessionId, SessionFileStore.ResultFileName)));

        using var credential = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                store.GetSessionFile(sessionId, SessionFileStore.CredentialFileName)));
        Assert.Equal(
            "replacement-secret",
            credential.RootElement.GetProperty("secret").GetString());
        Assert.DoesNotContain(
            Directory.EnumerateFiles(sessionDirectory),
            path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "stdout.log")));
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "stderr.log")));
    }

    [Fact]
    public async Task UsesRequiredUnixModesWhenUnixPermissionsAreAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows does not expose POSIX mode bits or numeric UID/GID. The
            // production implementation intentionally relies on ACLs/service identity there.
            return;
        }

        var sessionId = Guid.NewGuid();
        var store = CreateStore();
        await store.WriteManifestAsync(
            sessionId,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            CancellationToken.None);
        await store.ReplaceCredentialAsync(
            sessionId,
            new SourceControlCredential("user", "secret", DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            File.GetUnixFileMode(store.GetSessionDirectory(sessionId)));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(
                store.GetSessionFile(sessionId, SessionFileStore.ManifestFileName)));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(
                store.GetSessionFile(sessionId, SessionFileStore.CredentialFileName)));
    }

    [Fact]
    public async Task RejectsSecretsInManifest()
    {
        var store = CreateStore();
        var secretManifest = Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"llm":{"geminiApiKey":"must-not-persist"}}""");

        await Assert.ThrowsAsync<JsonException>(
            () => store.WriteManifestAsync(
                Guid.NewGuid(),
                secretManifest,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-session-file")]
    [InlineData("../manifest.json")]
    public void RejectsFilesOutsideContract(string fileName)
    {
        var store = CreateStore();
        Assert.Throws<ArgumentException>(() => store.GetSessionFile(Guid.NewGuid(), fileName));
    }

    [Fact]
    public void RejectsFilesystemRootAsSessionRoot()
    {
        var filesystemRoot = Path.GetPathRoot(rootDirectory)!;
        Assert.Throws<ArgumentException>(
            () => new SessionFileStore(
                new SessionFileStoreOptions { RootDirectory = filesystemRoot }));
    }

    [Fact]
    public void RejectsRelativeSessionRoot()
    {
        Assert.Throws<ArgumentException>(
            () => new SessionFileStore(
                new SessionFileStoreOptions { RootDirectory = "relative-sessions" }));
    }

    [Fact]
    public async Task DeletesCredentialAtTerminalState()
    {
        var sessionId = Guid.NewGuid();
        var store = CreateStore();
        await store.ReplaceCredentialAsync(
            sessionId,
            new SourceControlCredential("user", "secret", DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);

        store.DeleteCredential(sessionId);

        Assert.False(
            File.Exists(store.GetSessionFile(sessionId, SessionFileStore.CredentialFileName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SessionFileStore CreateStore() =>
        new(
            new SessionFileStoreOptions
            {
                RootDirectory = rootDirectory,
                OwnerUserId = SessionFileStore.GetEffectiveUserId(),
                OwnerGroupId = SessionFileStore.GetEffectiveGroupId(),
            });
}
