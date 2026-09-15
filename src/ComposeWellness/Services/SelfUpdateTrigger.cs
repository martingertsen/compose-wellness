using ComposeWellness.Configuration;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Services;

/// <summary>Requests a self-update from the root-owned helper unit.</summary>
public interface ISelfUpdateTrigger
{
    /// <summary>True when the setting allows it and the helper's trigger file path is configured.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Creates the trigger file. Throws <see cref="InvalidOperationException"/> when disabled and
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the file cannot be written.
    /// </summary>
    Task RequestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The web service cannot replace its own files or restart itself; it runs as an unprivileged
/// user. It only creates a file that compose-wellness-update.path watches. The root helper ignores
/// the content and decides on its own what to download, so nothing written here can influence
/// what gets installed.
/// </summary>
public sealed class SelfUpdateTrigger(IOptions<ComposeWellnessOptions> options) : ISelfUpdateTrigger
{
    private readonly string? _path = string.IsNullOrWhiteSpace(options.Value.SelfUpdateTriggerFile) ? null : options.Value.SelfUpdateTriggerFile;
    private readonly bool _allowed = options.Value.AllowSelfUpdate;

    public bool Enabled => _allowed && _path is not null;

    public async Task RequestAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Self-update is disabled.");
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "\n";
        await File.WriteAllTextAsync(_path!, stamp, cancellationToken);
    }
}
