using ComposeWellness.Configuration;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Services;

/// <summary>
/// Writes the complete log of each update to disk when a log directory is configured and
/// deletes the oldest files so the directory does not grow forever.
/// </summary>
public sealed class UpdateLogArchive(IOptions<ComposeWellnessOptions> options, ILogger<UpdateLogArchive> logger)
{
    private const string FilePrefix = "update-";
    private const string FileExtension = ".log";

    private string? Directory => string.IsNullOrWhiteSpace(options.Value.LogDirectory) ? null : options.Value.LogDirectory;

    /// <summary>Opens the log file for an update starting now, or returns null when archiving is disabled or fails.</summary>
    public TextWriter? Open(DateTimeOffset startedAt)
    {
        if (Directory is not { } directory)
        {
            return null;
        }

        var path = Path.Combine(directory, $"{FilePrefix}{startedAt:yyyyMMdd-HHmmss}{FileExtension}");

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            return new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Archiving is a convenience; a broken log directory must not block updates.
            logger.LogWarning(ex, "Could not open update log file {Path}. Continuing without a disk log.", path);
            return null;
        }
    }

    /// <summary>Deletes archived logs beyond the configured retention count, oldest first.</summary>
    public void Prune()
    {
        if (Directory is not { } directory || !System.IO.Directory.Exists(directory))
        {
            return;
        }

        try
        {
            // File names embed the start time, so ordinal descending order is newest first.
            var stale = System.IO.Directory.GetFiles(directory, $"{FilePrefix}*{FileExtension}")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(Math.Max(0, options.Value.RetainedLogFiles));

            foreach (var file in stale)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prune old update logs in {Directory}.", directory);
        }
    }
}
