using ComposeWellness.Configuration;
using ComposeWellness.Services;
using ComposeWellness.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Tests;

public sealed class SelfUpdateTriggerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static SelfUpdateTrigger Create(bool allow, string? triggerFile) =>
        new(Options.Create(new ComposeWellnessOptions { AllowSelfUpdate = allow, SelfUpdateTriggerFile = triggerFile }));

    [Fact]
    public async Task Writes_a_timestamp_into_the_trigger_file()
    {
        var file = Path.Combine(_temp.Path, "self-update.request");
        var trigger = Create(allow: true, file);

        Assert.True(trigger.Enabled);
        await trigger.RequestAsync(CancellationToken.None);

        Assert.True(File.Exists(file));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\n?$", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Overwrites_an_existing_trigger_file()
    {
        var file = Path.Combine(_temp.Path, "self-update.request");
        File.WriteAllText(file, "stale");

        await Create(allow: true, file).RequestAsync(CancellationToken.None);

        Assert.DoesNotContain("stale", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Disabled_when_not_allowed()
    {
        var trigger = Create(allow: false, Path.Combine(_temp.Path, "self-update.request"));

        Assert.False(trigger.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => trigger.RequestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Disabled_without_a_trigger_file_path(string? path)
    {
        var trigger = Create(allow: true, path);

        Assert.False(trigger.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => trigger.RequestAsync(CancellationToken.None));
    }

    public void Dispose() => _temp.Dispose();
}
