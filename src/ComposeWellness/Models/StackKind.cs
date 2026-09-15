namespace ComposeWellness.Models;

/// <summary>How a discovered directory should be updated.</summary>
public enum StackKind
{
    /// <summary>Neither update.sh nor a supported Compose file exists. The directory is skipped.</summary>
    None,

    /// <summary>An update.sh script exists and is fully responsible for the update.</summary>
    CustomScript,

    /// <summary>A supported Compose file exists and the standard pull/up procedure applies.</summary>
    Compose,
}
