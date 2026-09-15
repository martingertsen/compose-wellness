using ComposeWellness.Configuration;
using ComposeWellness.Services;
using ComposeWellness.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Tests;

public sealed class RootDirectoryProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private RootDirectoryProvider Create(string configuredRoot, bool allowChange = true) =>
        new(Options.Create(new ComposeWellnessOptions
        {
            RootDirectory = configuredRoot,
            DataDirectory = _temp.CreateDirectory("data"),
            AllowRootDirectoryChange = allowChange,
        }), NullLogger<RootDirectoryProvider>.Instance);

    [Fact]
    public void Uses_configured_directory_when_nothing_was_saved()
    {
        var configured = _temp.CreateDirectory("configured");

        var provider = Create(configured);

        Assert.Equal(configured, provider.Current);
        Assert.Equal(configured, provider.Configured);
    }

    [Fact]
    public async Task Saved_directory_overrides_configuration_and_survives_a_restart()
    {
        var configured = _temp.CreateDirectory("configured");
        var other = _temp.CreateDirectory("other");

        await Create(configured).SetAsync(other, CancellationToken.None);
        var restarted = Create(configured);

        Assert.Equal(other, restarted.Current);
        Assert.Equal(configured, restarted.Configured);
    }

    [Fact]
    public async Task Rejects_relative_and_missing_directories()
    {
        var provider = Create(_temp.CreateDirectory("configured"));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetAsync("relative/path", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetAsync(Path.Combine(_temp.Path, "does-not-exist"), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_changes_when_disabled_by_configuration()
    {
        var provider = Create(_temp.CreateDirectory("configured"), allowChange: false);

        Assert.False(provider.CanChange);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SetAsync(_temp.CreateDirectory("other"), CancellationToken.None));
    }

    [Fact]
    public void Ignores_a_corrupt_settings_file()
    {
        var configured = _temp.CreateDirectory("configured");
        _temp.CreateFile("data/settings.json", "not json");

        var provider = Create(configured);

        Assert.Equal(configured, provider.Current);
    }

    public void Dispose() => _temp.Dispose();
}
