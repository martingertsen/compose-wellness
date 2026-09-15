using ComposeWellness.Configuration;
using ComposeWellness.Models;
using ComposeWellness.Services;
using ComposeWellness.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Tests;

/// <summary>
/// Whether containers were actually recreated is derived from the container ids of the project
/// before and after the update: a recreated container always gets a new id.
/// </summary>
public sealed class RecreationDetectionTests
{
    private const string Dir = "/opt/stacks/example";

    private static DiscoveredStack Compose() => new("example", Dir, StackKind.Compose, "compose.yml");
    private static DiscoveredStack ScriptWithCompose() => new("example", Dir, StackKind.CustomScript, "compose.yml");
    private static DiscoveredStack ScriptOnly() => new("example", Dir, StackKind.CustomScript, null);

    private static StackUpdateService Service(IProcessRunner runner) =>
        new(runner, Options.Create(new ComposeWellnessOptions()));

    private static FakeProcessRunner ComposeRunner(string idsBefore, string idsAfter) => new FakeProcessRunner()
        .OnSequence("compose ps -a -q", idsBefore, idsAfter)
        .On("compose ps -q --status running", exitCode: 0, stdout: "running\n")
        .On("compose --ansi never pull", exitCode: 0)
        .On("compose --ansi never up", exitCode: 0);

    [Fact]
    public async Task Compose_update_reports_how_many_containers_got_new_ids()
    {
        var runner = ComposeRunner("aaa\nbbb\n", "aaa\nccc\n");

        var result = await Service(runner).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Success, result.Outcome);
        Assert.Equal(1, result.RecreatedContainers);
        Assert.Equal("Recreated 1 of 2 containers", result.Message);
    }

    [Fact]
    public async Task Compose_update_with_unchanged_ids_reports_no_change()
    {
        var runner = ComposeRunner("aaa\nbbb\n", "bbb\naaa\n");

        var result = await Service(runner).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Success, result.Outcome);
        Assert.Equal(0, result.RecreatedContainers);
        Assert.Equal("No change", result.Message);
    }

    [Fact]
    public async Task Container_id_listing_is_not_shown_in_the_live_log()
    {
        var log = new RecordingUpdateLog();

        await Service(ComposeRunner("aaa\n", "bbb\n")).UpdateStackAsync(Compose(), log, CancellationToken.None);

        Assert.DoesNotContain(log.ProcessLines, line => line is "aaa" or "bbb");
    }

    [Fact]
    public async Task Custom_script_next_to_a_compose_file_gets_the_same_detection()
    {
        var runner = new FakeProcessRunner()
            .OnSequence("compose ps -a -q", "aaa\n", "zzz\n")
            .On("bash ./update.sh", exitCode: 0);

        var result = await Service(runner).UpdateStackAsync(ScriptWithCompose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Success, result.Outcome);
        Assert.Equal(1, result.RecreatedContainers);
        Assert.Equal("Recreated 1 of 1 container", result.Message);
    }

    [Fact]
    public async Task Custom_script_without_a_compose_file_does_not_query_compose()
    {
        var runner = new FakeProcessRunner().On("bash ./update.sh", exitCode: 0);

        var result = await Service(runner).UpdateStackAsync(ScriptOnly(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Success, result.Outcome);
        Assert.Null(result.Message);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Failing_id_listing_does_not_fail_the_stack()
    {
        var runner = new FakeProcessRunner()
            .On("compose ps -a -q", exitCode: 1, "", "some compose error")
            .On("bash ./update.sh", exitCode: 0);

        var result = await Service(runner).UpdateStackAsync(ScriptWithCompose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Success, result.Outcome);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task Failed_script_keeps_the_exit_code_message_even_if_containers_changed()
    {
        var runner = new FakeProcessRunner()
            .OnSequence("compose ps -a -q", "aaa\n", "bbb\n")
            .On("bash ./update.sh", exitCode: 2);

        var result = await Service(runner).UpdateStackAsync(ScriptWithCompose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Failed, result.Outcome);
        Assert.Equal("update.sh exited with code 2", result.Message);
        Assert.Equal(1, result.RecreatedContainers);
    }
}
