using System.Reflection;

namespace ComposeWellness.Services;

/// <summary>The version of the running application, as shown in the footer and compared to releases.</summary>
public sealed class ApplicationVersion
{
    public ApplicationVersion(string text)
    {
        Text = text;
        Value = Version.TryParse(text, out var parsed) ? parsed : new Version(0, 0, 0);
    }

    /// <summary>The version from the project file, for example "1.1.0".</summary>
    public string Text { get; }

    public Version Value { get; }

    /// <summary>
    /// The informational version is the &lt;Version&gt; from the project file, possibly followed by
    /// "+&lt;commit&gt;" added by the SDK; only the version itself is used.
    /// </summary>
    public static ApplicationVersion FromAssembly()
    {
        var informational = typeof(ApplicationVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return new ApplicationVersion(informational.Split('+')[0]);
    }
}
