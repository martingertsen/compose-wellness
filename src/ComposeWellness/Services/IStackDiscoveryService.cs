using ComposeWellness.Models;

namespace ComposeWellness.Services;

public interface IStackDiscoveryService
{
    /// <summary>The directory that <see cref="DiscoverStacks"/> scans.</summary>
    string RootDirectory { get; }

    /// <summary>
    /// Enumerates the immediate child directories of the configured root, sorted by name.
    /// Throws <see cref="DirectoryNotFoundException"/> when the root does not exist.
    /// </summary>
    IReadOnlyList<DiscoveredStack> DiscoverStacks();
}
