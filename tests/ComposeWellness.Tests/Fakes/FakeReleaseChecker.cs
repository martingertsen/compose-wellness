using ComposeWellness.Services;

namespace ComposeWellness.Tests.Fakes;

public sealed class FakeReleaseChecker : IReleaseChecker
{
    public string Repository { get; set; } = "owner/repo";
    public Version? LatestVersion { get; set; }
    public string? ReleaseUrl { get; set; }
    public bool UpdateAvailable { get; set; }
    public string? LastError { get; set; }
}
