using System.Diagnostics;

using AkosFabric.Application.RepositoryProfiles.Models;

namespace AkosFabric.Infrastructure.RepositoryProfiles;

internal static class GitProfileRevisionResolver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<string> ResolveAsync(
        string profileRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(profileRoot);
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new RepositoryProfileException(
                    "Git did not start while resolving the profile revision.");
            }
        }
        catch (Exception exception) when (exception is not RepositoryProfileException)
        {
            throw new RepositoryProfileException(
                "Cannot start Git to resolve the profile checkout revision.",
                exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(Timeout);

        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new RepositoryProfileException(
                "Timed out while resolving the profile checkout revision.");
        }

        var revision = (await standardOutput).Trim();
        var error = (await standardError).Trim();
        if (process.ExitCode != 0)
        {
            throw new RepositoryProfileException(
                $"Cannot resolve the profile checkout revision: {error}");
        }

        if (revision.Length is not (40 or 64) ||
            revision.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new RepositoryProfileException(
                "The profile checkout revision is not a full Git commit SHA.");
        }

        return revision.ToLowerInvariant();
    }
}
