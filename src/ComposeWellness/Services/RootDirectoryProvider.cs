using System.Text.Json;
using ComposeWellness.Configuration;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Services;

/// <summary>Resolves the directory that is scanned for stacks and lets the web UI change it.</summary>
public interface IRootDirectoryProvider
{
    /// <summary>The directory in effect right now.</summary>
    string Current { get; }

    /// <summary>The directory from configuration, used when nothing was chosen in the UI.</summary>
    string Configured { get; }

    bool CanChange { get; }

    /// <summary>
    /// Validates and persists a new directory. Throws <see cref="ArgumentException"/> for an
    /// invalid path and <see cref="InvalidOperationException"/> when changes are disabled.
    /// </summary>
    Task SetAsync(string rootDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps a UI chosen root directory in settings.json inside the data directory, so it survives
/// service restarts and upgrades without touching the read-only application files.
/// </summary>
public sealed class RootDirectoryProvider : IRootDirectoryProvider
{
    private const string SettingsFileName = "settings.json";

    private readonly string _settingsPath;
    private readonly ILogger<RootDirectoryProvider> _logger;
    private readonly object _gate = new();
    private string _current;

    public RootDirectoryProvider(IOptions<ComposeWellnessOptions> options, ILogger<RootDirectoryProvider> logger)
    {
        _logger = logger;
        Configured = options.Value.RootDirectory;
        CanChange = options.Value.AllowRootDirectoryChange;

        var dataDirectory = string.IsNullOrWhiteSpace(options.Value.DataDirectory)
            ? AppContext.BaseDirectory
            : options.Value.DataDirectory;
        _settingsPath = Path.Combine(dataDirectory, SettingsFileName);

        _current = LoadSaved() ?? Configured;
    }

    public string Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public string Configured { get; }

    public bool CanChange { get; }

    public async Task SetAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        if (!CanChange)
        {
            throw new InvalidOperationException("Changing the root directory is disabled by configuration (ComposeWellness:AllowRootDirectoryChange).");
        }

        if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathRooted(rootDirectory))
        {
            throw new ArgumentException("The root directory must be an absolute path.");
        }

        var fullPath = Path.GetFullPath(rootDirectory.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new ArgumentException($"The directory {fullPath} does not exist on the Docker host.");
        }

        // Write to a temporary file first so a crash mid-write cannot leave a truncated settings file.
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporary = _settingsPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new SavedSettings(fullPath)), cancellationToken);
        File.Move(temporary, _settingsPath, overwrite: true);

        lock (_gate)
        {
            _current = fullPath;
        }

        _logger.LogInformation("Root directory changed to {RootDirectory}", fullPath);
    }

    private string? LoadSaved()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(_settingsPath));
            if (saved?.RootDirectory is { } path && Path.IsPathRooted(path))
            {
                return path;
            }

            _logger.LogWarning("Ignoring {Path}: it does not contain an absolute root directory.", _settingsPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable settings file {Path}.", _settingsPath);
        }

        return null;
    }

    private sealed record SavedSettings(string RootDirectory);
}
