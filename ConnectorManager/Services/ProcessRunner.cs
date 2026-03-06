using System.Diagnostics;
using System.Text;

namespace ConnectorManager.Services;

/// <summary>
/// Runs CLI commands (dotnet build, dotnet publish, etc.) and streams output in real-time.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>
    /// Executes a command and streams stdout/stderr to the callback.
    /// Returns the exit code.
    /// </summary>
    public async Task<int> RunAsync(
        string command,
        string arguments,
        string workingDirectory,
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onOutput);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var outputComplete = new TaskCompletionSource<bool>();
        var errorComplete = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
            else
            {
                outputComplete.TrySetResult(true);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput($"[stderr] {e.Data}");
            }
            else
            {
                errorComplete.TrySetResult(true);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(outputComplete.Task, errorComplete.Task).ConfigureAwait(false);

        return process.ExitCode;
    }

    /// <summary>
    /// Runs a dotnet command with the specified arguments and working directory.
    /// </summary>
    public Task<int> RunDotnetAsync(
        string arguments,
        string workingDirectory,
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        return RunAsync("dotnet", arguments, workingDirectory, onOutput, cancellationToken);
    }
}
