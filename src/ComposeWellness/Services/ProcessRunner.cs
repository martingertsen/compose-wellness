using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ComposeWellness.Services;

/// <summary>
/// Starts executables directly (no shell, arguments passed as a list) and streams their output
/// line by line while they run.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// How long to wait for stdout/stderr to close after the process has exited. A script that
    /// leaves a background process attached to its output would otherwise block the update forever.
    /// </summary>
    public static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new ProcessStartException($"Could not start {request.FileName}.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new ProcessStartException($"Could not start {request.FileName}: {ex.Message}", ex);
        }

        // Nothing ever answers a prompt, so closing stdin makes interactive scripts fail fast instead of hanging.
        process.StandardInput.Close();

        var standardOutput = new StringBuilder();
        var outputLock = new object();

        var stdoutTask = PumpAsync(process.StandardOutput, line =>
        {
            lock (outputLock)
            {
                standardOutput.AppendLine(line);
            }

            onOutputLine?.Invoke(line);
        });
        var stderrTask = PumpAsync(process.StandardError, line => onOutputLine?.Invoke(line));

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await DrainOutputAsync(stdoutTask, stderrTask, onOutputLine);
            throw;
        }

        // The pipes can still hold buffered output after the process has exited.
        await DrainOutputAsync(stdoutTask, stderrTask, onOutputLine);

        return new ProcessResult(process.ExitCode, standardOutput.ToString());
    }

    private static async Task DrainOutputAsync(Task stdoutTask, Task stderrTask, Action<string>? onOutputLine)
    {
        var pumps = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(pumps, Task.Delay(OutputDrainTimeout)) == pumps)
        {
            await pumps;
            return;
        }

        // A detached child still holds the pipe. Its future output is dropped so the update can continue.
        onOutputLine?.Invoke("(a background process is still attached to the output; Compose Wellness stops waiting and continues)");
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        // Runs until the pipe closes. Not cancelled through the token on purpose: after a kill the
        // pipe closes by itself, and ending early would drop the last lines of output.
        while (await reader.ReadLineAsync() is { } line)
        {
            onLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process finished between the check and the kill; nothing left to do.
        }
    }
}
