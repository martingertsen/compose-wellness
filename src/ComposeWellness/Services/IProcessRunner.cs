namespace ComposeWellness.Services;

/// <summary>Describes a process to start directly, without going through a shell.</summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>The command as a human would type it, for log messages only.</summary>
    public string DisplayCommand => string.Join(" ", new[] { FileName }.Concat(Arguments));
}

/// <param name="StandardOutput">Complete stdout, needed when the caller has to inspect the output.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput);

/// <summary>Thrown when the executable could not be started at all, for example because it is not installed.</summary>
public sealed class ProcessStartException(string message, Exception? inner = null) : Exception(message, inner);

public interface IProcessRunner
{
    /// <summary>
    /// Runs the process to completion. Every stdout and stderr line is passed to
    /// <paramref name="onOutputLine"/> as soon as it arrives. Cancelling the token kills the process.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? onOutputLine, CancellationToken cancellationToken);
}
