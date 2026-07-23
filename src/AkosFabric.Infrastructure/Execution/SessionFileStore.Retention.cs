namespace AkosFabric.Infrastructure.Execution;

internal sealed record SessionDirectoryEntry(
    Guid RepositorySessionId,
    string DirectoryPath,
    DateTimeOffset RetentionStartedAt);

public sealed partial class SessionFileStore
{
    internal IReadOnlyList<SessionDirectoryEntry> ListSessionDirectories(
        bool deleteCredentialsBeforeValidation = false)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return [];
        }

        RejectReparsePoint(rootDirectory);
        var sessions = new List<SessionDirectoryEntry>();
        foreach (string configuredPath in
                 Directory.EnumerateDirectories(rootDirectory))
        {
            string path = Path.GetFullPath(configuredPath);
            EnsureDescendant(path);
            string name = Path.GetFileName(path);
            if (!Guid.TryParseExact(name, "D", out Guid sessionId)
                || sessionId == Guid.Empty
                || !string.Equals(
                    name,
                    sessionId.ToString("D"),
                    PathComparison))
            {
                continue;
            }

            string expected = GetSessionDirectory(sessionId);
            if (!string.Equals(path, expected, PathComparison))
            {
                throw new InvalidOperationException(
                    $"Session directory '{path}' is not canonical.");
            }

            if (deleteCredentialsBeforeValidation)
            {
                DeleteCredential(sessionId);
            }

            ValidateSessionTree(path);
            sessions.Add(
                new SessionDirectoryEntry(
                    sessionId,
                    path,
                    new DateTimeOffset(
                        Directory.GetLastWriteTimeUtc(path),
                        TimeSpan.Zero)));
        }

        return sessions
            .OrderBy(session => session.RetentionStartedAt)
            .ThenBy(session => session.RepositorySessionId)
            .ToArray();
    }

    internal void ScheduleRetention(
        Guid repositorySessionId,
        DateTimeOffset retentionStartedAt)
    {
        string path = GetSessionDirectory(repositorySessionId);
        if (!Directory.Exists(path))
        {
            return;
        }

        DeleteCredential(repositorySessionId);
        ValidateSessionTree(path);
        Directory.SetLastWriteTimeUtc(
            path,
            retentionStartedAt.UtcDateTime);
    }

    internal void DeleteSessionDirectory(Guid repositorySessionId)
    {
        string path = GetSessionDirectory(repositorySessionId);
        if (!Directory.Exists(path))
        {
            return;
        }

        DeleteCredential(repositorySessionId);
        ValidateSessionTree(path);
        Directory.Delete(path, recursive: true);
    }

    private void ValidateSessionTree(string sessionDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The session root '{rootDirectory}' does not exist.");
        }

        RejectReparsePoint(rootDirectory);
        EnsureDescendant(sessionDirectory);
        RejectReparsePoint(sessionDirectory);
        var pending = new Stack<string>();
        pending.Push(sessionDirectory);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in
                     Directory.EnumerateFileSystemEntries(directory))
            {
                string fullPath = Path.GetFullPath(entry);
                EnsureDescendant(fullPath);
                FileAttributes attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Session retention refuses to traverse a reparse " +
                        $"point: {fullPath}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(fullPath);
                }
            }
        }
    }
}
