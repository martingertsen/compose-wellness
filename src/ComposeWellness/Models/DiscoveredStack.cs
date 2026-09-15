namespace ComposeWellness.Models;

/// <summary>An immediate child directory of the configured root directory.</summary>
/// <param name="Name">Directory name, used as the stack name in logs and results.</param>
/// <param name="Directory">Absolute path of the stack directory.</param>
/// <param name="Kind">Which update procedure applies.</param>
/// <param name="ComposeFile">File name of the detected Compose file, if any.</param>
public sealed record DiscoveredStack(string Name, string Directory, StackKind Kind, string? ComposeFile);
