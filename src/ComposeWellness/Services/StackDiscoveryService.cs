using ComposeWellness.Models;

namespace ComposeWellness.Services;

/// <summary>
/// Finds stacks by looking at the immediate child directories of the configured root.
/// Discovery is deliberately non-recursive: a Compose file two levels down (for example in an
/// archive folder) must never cause anything to be updated.
/// </summary>
public sealed class StackDiscoveryService(IRootDirectoryProvider rootDirectory) : IStackDiscoveryService
{
    public const string UpdateScriptName = "update.sh";

    public string RootDirectory => rootDirectory.Current;

    public static readonly IReadOnlyList<string> ComposeFileNames =
    [
        "compose.yml",
        "compose.yaml",
        "docker-compose.yml",
        "docker-compose.yaml",
    ];

    public IReadOnlyList<DiscoveredStack> DiscoverStacks()
    {
        var root = Path.GetFullPath(RootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root directory {root} was not found.");
        }

        var stacks = new List<DiscoveredStack>();

        // EnumerateDirectories without a SearchOption only returns the top level, which is exactly
        // the "RootDirectory/*/" rule. Files directly under the root are ignored.
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            stacks.Add(Classify(directory));
        }

        // Ordinal sorting gives the same order as "LC_ALL=C ls", so runs are predictable
        // regardless of the culture the service happens to run under.
        stacks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return stacks;
    }

    private static DiscoveredStack Classify(string directory)
    {
        var name = Path.GetFileName(directory);
        var composeFile = ComposeFileNames.FirstOrDefault(file => File.Exists(Path.Combine(directory, file)));

        if (File.Exists(Path.Combine(directory, UpdateScriptName)))
        {
            return new DiscoveredStack(name, directory, StackKind.CustomScript, composeFile);
        }

        return composeFile is null
            ? new DiscoveredStack(name, directory, StackKind.None, null)
            : new DiscoveredStack(name, directory, StackKind.Compose, composeFile);
    }
}
