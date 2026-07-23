using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Infrastructure.Execution;

public sealed partial class SessionFileStore
{
    public const string ManifestFileName = "manifest.json";
    public const string CredentialFileName = "source-control-credential.json";
    public const string ResultFileName = "result.json";
    public const string StandardOutputFileName = "stdout.log";
    public const string StandardErrorFileName = "stderr.log";
    public const string ControlFileName = "control.json";

    private const UnixFileMode SessionDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode SharedFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private const UnixFileMode CredentialFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions CredentialJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string rootDirectory;
    private readonly int ownerUserId;
    private readonly int ownerGroupId;

    public SessionFileStore(SessionFileStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            throw new ArgumentException("The session root directory is required.", nameof(options));
        }

        if (!Path.IsPathFullyQualified(options.RootDirectory))
        {
            throw new ArgumentException(
                "The session root directory must be an absolute path.",
                nameof(options));
        }

        if (options.OwnerUserId < 0 || options.OwnerGroupId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The session owner UID and GID must be non-negative.");
        }

        rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        if (Path.GetPathRoot(rootDirectory)?.Equals(rootDirectory, PathComparison) is true)
        {
            throw new ArgumentException(
                "The session root cannot be a filesystem root.",
                nameof(options));
        }

        ownerUserId = options.OwnerUserId;
        ownerGroupId = options.OwnerGroupId;
    }

    public string RootDirectory => rootDirectory;

    internal static int GetEffectiveUserId() =>
        OperatingSystem.IsWindows() ? 10001 : UnixOwnership.GetEffectiveUserId();

    internal static int GetEffectiveGroupId() =>
        OperatingSystem.IsWindows() ? 10001 : UnixOwnership.GetEffectiveGroupId();

    public string GetSessionDirectory(Guid repositorySessionId)
    {
        ValidateSessionId(repositorySessionId);
        var path = Path.GetFullPath(Path.Combine(rootDirectory, repositorySessionId.ToString("D")));
        EnsureDescendant(path);
        return path;
    }

    public string GetSessionFile(Guid repositorySessionId, string fileName)
    {
        if (fileName is not (
            ManifestFileName or
            CredentialFileName or
            ResultFileName or
            StandardOutputFileName or
            StandardErrorFileName or
            ControlFileName))
        {
            throw new ArgumentException("The file name is not part of the session contract.", nameof(fileName));
        }

        return Path.Combine(GetSessionDirectory(repositorySessionId), fileName);
    }

    public string EnsureSessionDirectory(Guid repositorySessionId)
    {
        Directory.CreateDirectory(rootDirectory);
        RejectReparsePoint(rootDirectory);

        var sessionDirectory = GetSessionDirectory(repositorySessionId);
        Directory.CreateDirectory(sessionDirectory);
        RejectReparsePoint(sessionDirectory);
        ApplyDirectoryAccess(sessionDirectory);
        return sessionDirectory;
    }

    public async Task WriteManifestAsync(
        Guid repositorySessionId,
        ReadOnlyMemory<byte> manifestJson,
        CancellationToken cancellationToken)
    {
        ValidateJsonObject(manifestJson, rejectManifestSecrets: true);
        await WriteAtomicAsync(
            GetSessionFile(repositorySessionId, ManifestFileName),
            manifestJson,
            SharedFileMode,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceCredentialAsync(
        Guid repositorySessionId,
        SourceControlCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.Username) ||
            string.IsNullOrEmpty(credential.Secret))
        {
            throw new ArgumentException("A username and secret are required.", nameof(credential));
        }

        var document = new CredentialDocument(
            credential.Username,
            credential.Secret,
            credential.ExpiresAt?.ToUniversalTime());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, CredentialJsonOptions);
        try
        {
            await WriteAtomicAsync(
                GetSessionFile(repositorySessionId, CredentialFileName),
                bytes,
                CredentialFileMode,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task WriteResultAsync(
        Guid repositorySessionId,
        ReadOnlyMemory<byte> resultJson,
        CancellationToken cancellationToken)
    {
        ValidateJsonObject(resultJson, rejectManifestSecrets: false);
        return WriteAtomicAsync(
            GetSessionFile(repositorySessionId, ResultFileName),
            resultJson,
            SharedFileMode,
            cancellationToken);
    }

    public async Task InitializeLogFilesAsync(
        Guid repositorySessionId,
        CancellationToken cancellationToken)
    {
        EnsureSessionDirectory(repositorySessionId);
        await EnsureFileAsync(
            GetSessionFile(repositorySessionId, StandardOutputFileName),
            SharedFileMode,
            cancellationToken).ConfigureAwait(false);
        await EnsureFileAsync(
            GetSessionFile(repositorySessionId, StandardErrorFileName),
            SharedFileMode,
            cancellationToken).ConfigureAwait(false);
    }

    public void DeleteCredential(Guid repositorySessionId)
    {
        var path = GetSessionFile(repositorySessionId, CredentialFileName);
        if (!File.Exists(path))
        {
            return;
        }

        RejectReparsePoint(rootDirectory);
        RejectReparsePoint(GetSessionDirectory(repositorySessionId));
        RejectReparsePoint(path);

        // Best effort overwrite reduces ordinary recovery risk; storage devices and
        // copy-on-write filesystems do not guarantee secure erasure.
        var length = new FileInfo(path).Length;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            var zeros = new byte[Math.Min(81920, Math.Max(1, length))];
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(zeros.Length, remaining);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }

            stream.Flush(flushToDisk: true);
        }

        File.Delete(path);
    }

    private async Task WriteAtomicAsync(
        string destinationPath,
        ReadOnlyMemory<byte> contents,
        UnixFileMode mode,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The destination has no parent directory.");
        EnsureSessionDirectory(Guid.Parse(Path.GetFileName(directory)));

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        EnsureDescendant(temporaryPath);

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ApplyFileAccess(temporaryPath, mode);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            ApplyFileAccess(destinationPath, mode);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task EnsureFileAsync(
        string path,
        UnixFileMode mode,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(
                         path,
                         FileMode.OpenOrCreate,
                         FileAccess.Write,
                         FileShare.ReadWrite,
                         bufferSize: 1,
                         FileOptions.Asynchronous))
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        ApplyFileAccess(path, mode);
    }

    private void ApplyDirectoryAccess(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, SessionDirectoryMode);
            UnixOwnership.Set(path, ownerUserId, ownerGroupId);
        }
    }

    private void ApplyFileAccess(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
            UnixOwnership.Set(path, ownerUserId, ownerGroupId);
        }
    }

    private void EnsureDescendant(string path)
    {
        var relative = Path.GetRelativePath(rootDirectory, path);
        if (relative is "." ||
            Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The session path escapes its configured root.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Session storage cannot use a reparse point: {path}");
        }
    }

    private static void ValidateSessionId(Guid repositorySessionId)
    {
        if (repositorySessionId == Guid.Empty)
        {
            throw new ArgumentException("The repository session ID cannot be empty.", nameof(repositorySessionId));
        }
    }

    private static void ValidateJsonObject(ReadOnlyMemory<byte> value, bool rejectManifestSecrets)
    {
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException("The session JSON document must be an object.");
        }

        if (rejectManifestSecrets)
        {
            RejectSecretProperty(document.RootElement);
        }
    }

    private static void RejectSecretProperty(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("geminiApiKey", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("sourceControlCredential", StringComparison.OrdinalIgnoreCase))
                {
                    throw new JsonException($"The manifest cannot contain the secret property '{property.Name}'.");
                }

                RejectSecretProperty(property.Value);
            }
        }
        else if (element.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectSecretProperty(item);
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record CredentialDocument(
        string Username,
        string Secret,
        DateTimeOffset? ExpiresAt);

    private static partial class UnixOwnership
    {
        public static int GetEffectiveUserId() => checked((int)GetEuid());

        public static int GetEffectiveGroupId() => checked((int)GetEgid());

        public static void Set(string path, int userId, int groupId)
        {
            if (Chown(path, userId, groupId) != 0)
            {
                throw new IOException(
                    $"Could not set UID/GID {userId}:{groupId} on '{path}' " +
                    $"(errno {Marshal.GetLastPInvokeError()}).");
            }
        }

        [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int Chown(string path, int owner, int group);

        [LibraryImport("libc", EntryPoint = "geteuid")]
        private static partial uint GetEuid();

        [LibraryImport("libc", EntryPoint = "getegid")]
        private static partial uint GetEgid();
    }
}
