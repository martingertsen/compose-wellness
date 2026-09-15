namespace ComposeWellness.Services;

/// <summary>What is known about the latest release of the configured GitHub repository.</summary>
public interface IReleaseChecker
{
    /// <summary>"owner/repo", or empty when the check is disabled.</summary>
    string Repository { get; }

    Version? LatestVersion { get; }

    /// <summary>Web page of the latest release, for the link in the UI.</summary>
    string? ReleaseUrl { get; }

    /// <summary>True when <see cref="LatestVersion"/> is newer than the running application.</summary>
    bool UpdateAvailable { get; }

    /// <summary>Why the last check failed, or null when it succeeded or never ran.</summary>
    string? LastError { get; }
}
