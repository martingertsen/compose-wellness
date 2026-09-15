using ComposeWellness.Configuration;
using ComposeWellness.Models;
using ComposeWellness.Services;
using ComposeWellness.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Tests;

public sealed class StackUpdateServiceTests
{
    private const string Dir = "/opt/stacks/example";

    private static DiscoveredStack Compose() => new("example", Dir, StackKind.Compose, "compose.yml");
    private static DiscoveredStack Script() => new("example", Dir, StackKind.CustomScript, null);
    private static DiscoveredStack Nothing() => new("example", Dir, StackKind.None, null);

    private static StackUpdateService Service(IProcessRunner runner, bool removeOrphans = false) =>
        new(runner, Options.Create(new ComposeWellnessOptions { RemoveOrphans = removeOrphans }));

    [Fact]
    public async Task Custom_script_is_run_with_bash_in_stack_directory_and_nothing_else()
    {
        var runner = new FakeProcessRunner().On("bash ./update.sh", exitCode: 0, "", "custom output");
        var log = new RecordingUpdateLog();

        var result = await Service(runner).UpdateStackAsync(Script(), log, CancellationToken.None);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("bash", request.FileName);
        Assert.Equal(["./update.sh"], request.Arguments);
        Assert.Equal(Dir, request.WorkingDirectory);
        Assert.Equal(StackOutcome.Success, result.Outcome);
        Assert.Contains("custom output", log.ProcessLines);
    }

    [Fact]
    public async Task Custom_script_non_zero_exit_code_is_a_failure()
    {
        var runner = new FakeProcessRunner().On("bash ./update.sh", exitCode: 3);

        var result = await Service(runner).UpdateStackAsync(Script(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Failed, result.Outcome);
        Assert.Contains("3", result.Message);
    }

    [Fact]
    public async Task Compose_stack_runs_pull_then_up_in_stack_directory()
    {
        var runner = new FakeProcessRunner()
            .On("compose ps", exitCode: 0, stdout: "abc123\n")
            .On("compose --ansi never pull", exitCode: 0, "", "example Pulled")
            .On("compose --ansi never up -d", exitCode: 0, "", "Container example Started");
        var log = new RecordingUpdateLog();

        var result = await Service(runner).UpdateStackAsync(Compose(), log, CancellationToken.None);

        Assert.Equal(StackOutcome.Success, result.Outcome);
        // running check, id baseline, pull, up, id listing after
        Assert.Equal(5, runner.Requests.Count);
        Assert.All(runner.Requests, r => Assert.Equal("docker", r.FileName));
        Assert.All(runner.Requests, r => Assert.Equal(Dir, r.WorkingDirectory));
        Assert.Equal(["compose", "--ansi", "never", "pull"], runner.Requests[2].Arguments);
        Assert.Equal(["compose", "--ansi", "never", "up", "-d"], runner.Requests[3].Arguments);
        Assert.Contains("example Pulled", log.ProcessLines);
        Assert.Contains("Container example Started", log.ProcessLines);
    }

    [Fact]
    public async Task Remove_orphans_is_only_passed_when_enabled()
    {
        var runner = new FakeProcessRunner()
            .On("compose ps", exitCode: 0, stdout: "abc123\n")
            .On("compose --ansi never pull", exitCode: 0)
            .On("compose --ansi never up", exitCode: 0);

        await Service(runner, removeOrphans: true).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(["compose", "--ansi", "never", "up", "-d", "--remove-orphans"], runner.Requests[3].Arguments);
    }

    [Fact]
    public async Task Compose_stack_with_no_running_containers_is_skipped_without_pull_or_up()
    {
        var runner = new FakeProcessRunner().On("compose ps", exitCode: 0, stdout: "\n");

        var result = await Service(runner).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Skipped, result.Outcome);
        Assert.Contains("not currently running", result.Message);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Running_check_asks_compose_for_running_container_ids_only()
    {
        var runner = new FakeProcessRunner().On("compose ps", exitCode: 0, stdout: "");

        await Service(runner).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(["compose", "ps", "-q", "--status", "running"], runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task Failing_pull_marks_stack_failed_and_skips_up()
    {
        var runner = new FakeProcessRunner()
            .On("compose ps", exitCode: 0, stdout: "abc123\n")
            .On("compose --ansi never pull", exitCode: 1, "", "error: manifest unknown");

        var result = await Service(runner).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Failed, result.Outcome);
        // running check, id baseline, pull
        Assert.Equal(3, runner.Requests.Count);
        Assert.DoesNotContain(runner.Requests, r => r.DisplayCommand.Contains(" up "));
    }

    [Fact]
    public async Task Failing_up_marks_stack_failed()
    {
        var runner = new FakeProcessRunner()
            .On("compose ps", exitCode: 0, stdout: "abc123\n")
            .On("compose --ansi never pull", exitCode: 0)
            .On("compose --ansi never up", exitCode: 1);

        var result = await Service(runner).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Failed, result.Outcome);
        Assert.Contains("up", result.Message);
    }

    [Fact]
    public async Task Failing_running_check_marks_stack_failed()
    {
        var runner = new FakeProcessRunner().On("compose ps", exitCode: 1, "", "no configuration file provided");

        var result = await Service(runner).UpdateStackAsync(Compose(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Failed, result.Outcome);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Directory_without_script_or_compose_is_skipped_without_running_anything()
    {
        var runner = new FakeProcessRunner();

        var result = await Service(runner).UpdateStackAsync(Nothing(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Skipped, result.Outcome);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Process_that_cannot_start_is_reported_as_failure_not_exception()
    {
        var runner = new ThrowingProcessRunner();

        var result = await Service(runner).UpdateStackAsync(Script(), new RecordingUpdateLog(), CancellationToken.None);

        Assert.Equal(StackOutcome.Failed, result.Outcome);
        Assert.Contains("bash", result.Message);
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? onOutputLine, CancellationToken cancellationToken)
            => throw new ProcessStartException($"Could not start '{request.FileName}'.");
    }
}
