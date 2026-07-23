using System.Diagnostics;
using System.Text;

namespace AkosFabric.Infrastructure.Execution;

internal sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StandardOutputPath = null,
    string? StandardErrorPath = null);

internal sealed record ProcessExecutionResult(
    int? ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError);

internal interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ProcessRunner : IProcessRunner
{
    private const int MaximumCapturedCharacters = 1024 * 1024;

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (invocation.Environment is not null)
        {
            foreach (var (name, value) in invocation.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start executable '{invocation.FileName}'.");
        }

        var stdoutTask = DrainAsync(
            process.StandardOutput,
            invocation.StandardOutputPath,
            CancellationToken.None);
        var stderrTask = DrainAsync(
            process.StandardError,
            invocation.StandardErrorPath,
            CancellationToken.None);

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var cancelledOutput = await stdoutTask.ConfigureAwait(false);
            var cancelledError = await stderrTask.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return new ProcessExecutionResult(
                ExitCode: null,
                TimedOut: true,
                StandardOutput: cancelledOutput,
                StandardError: cancelledError);
        }

        return new ProcessExecutionResult(
            process.ExitCode,
            TimedOut: false,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static async Task<string> DrainAsync(
        StreamReader reader,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        FileStream? file = null;
        if (outputPath is not null)
        {
            file = new FileStream(
                outputPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }

        await using var output = file;
        var captured = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (captured.Length < MaximumCapturedCharacters)
            {
                var remaining = MaximumCapturedCharacters - captured.Length;
                captured.Append(buffer, 0, Math.Min(count, remaining));
            }

            if (output is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(new string(buffer, 0, count));
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }

        if (output is not null)
        {
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        return captured.ToString();
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }
}
