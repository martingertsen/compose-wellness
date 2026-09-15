namespace ComposeWellness.Tests.Fakes;

/// <summary>Creates an isolated temporary directory tree that is deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "compose-wellness-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public string CreateDirectory(string relativePath)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void CreateFile(string relativePath, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup; a leftover temp directory is not a test failure.
        }
    }
}
