namespace ComposeWellness.Configuration;

/// <summary>
/// Strongly typed settings bound from the "ComposeWellness" configuration section.
/// Every value can also be supplied through environment variables, for example
/// ComposeWellness__RootDirectory=/opt/stacks.
/// </summary>
public sealed class ComposeWellnessOptions
{
    public const string SectionName = "ComposeWellness";

    /// <summary>
    /// Directory whose immediate child directories are treated as Docker Compose stacks.
    /// Only this level is inspected; nothing deeper is ever searched. A directory chosen in the
    /// web UI (stored in <see cref="DataDirectory"/>) takes precedence over this value.
    /// </summary>
    public string RootDirectory { get; set; } = "/opt/stacks";

    /// <summary>
    /// Whether the root directory may be changed from the web UI. Off by default because the page
    /// has no authentication: anyone who can reach it could otherwise point Compose Wellness at any
    /// directory on the host.
    /// </summary>
    public bool AllowRootDirectoryChange { get; set; }

    /// <summary>
    /// Whether "docker compose up" runs with --remove-orphans, which deletes containers that belong
    /// to the project but are no longer defined in its Compose file. Off by default because it
    /// removes containers rather than only updating them.
    /// </summary>
    public bool RemoveOrphans { get; set; }

    /// <summary>
    /// Directory for settings changed at runtime (settings.json). Defaults to the application
    /// directory; the systemd unit points it at the service state directory instead.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Optional directory where the full log of every completed update is written.
    /// When null or empty, logs are only kept in memory.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// Maximum number of log lines kept in memory for the current or most recent update.
    /// Older lines are dropped so memory usage stays bounded.
    /// </summary>
    public int MaxLogLines { get; set; } = 5000;

    /// <summary>
    /// Number of on-disk update logs to keep when <see cref="LogDirectory"/> is configured.
    /// Older files are deleted after each update.
    /// </summary>
    public int RetainedLogFiles { get; set; } = 20;
}
