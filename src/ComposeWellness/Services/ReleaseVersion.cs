using System.Text.RegularExpressions;

namespace ComposeWellness.Services;

/// <summary>Turns a GitHub release tag such as "v1.2.0" into a <see cref="Version"/>.</summary>
public static partial class ReleaseVersion
{
    /// <summary>
    /// Accepts "1.2.0" with an optional leading "v" or "V". Pre-release suffixes and two or four
    /// part versions are rejected so a stray tag never counts as an update.
    /// </summary>
    public static bool TryParse(string? tag, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var match = TagPattern().Match(tag.Trim());
        if (!match.Success)
        {
            return false;
        }

        version = Version.Parse(match.Groups["version"].Value);
        return true;
    }

    [GeneratedRegex(@"^[vV]?(?<version>\d+\.\d+\.\d+)\z")]
    private static partial Regex TagPattern();
}
