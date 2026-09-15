using ComposeWellness.Configuration;
using ComposeWellness.Models;
using ComposeWellness.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Tests;

public sealed class UpdateCoordinatorTests
{
    private static DiscoveredStack Compose(string name) => new(name, $"/docker/{name}", StackKind.Compose, "compose.yml");

    private static UpdateCoordinator CreateCoordinator(IStackDiscoveryService discovery, IStackUpdateService updateService, int maxLogLines = 5000) =>
        new(discovery, updateService, new UpdateLogArchive(Options.Create(new ComposeWellnessOptions()), NullLogger<UpdateLogArchive>.Instance),
            Options.Create(new ComposeWellnessOptions { RootDirectory = "/docker", MaxLogLines = maxLogLines }), NullLogger<UpdateCoordinator>.Instance);

    private static async Task RunToCompletion(UpdateCoordinator coordinator)
    {
        Assert.True(coordinator.TryStartUpdate());
        await coordinator.CurrentRun!;
    }

    [Fact]
    public void Status_is_idle_before_first_update()
    {
        var coordinator = CreateCoordinator(new FixedDiscovery(), new ScriptedUpdateService());

        var status = coordinator.GetStatus();

        Assert.Equal(UpdateState.Idle, status.State);
        Assert.Null(status.SessionId);
        Assert.Empty(status.Results);
    }

    [Fact]
    public async Task Second_start_is_rejected_while_an_update_is_running()
    {
        var gate = new TaskCompletionSource();
        var updateService = new ScriptedUpdateService { BeforeEachStack = () => gate.Task };
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("a")), updateService);

        Assert.True(coordinator.TryStartUpdate());
        Assert.False(coordinator.TryStartUpdate());
        Assert.Equal(UpdateState.Running, coordinator.GetStatus().State);

        gate.SetResult();
        await coordinator.CurrentRun!;

        Assert.True(coordinator.TryStartUpdate());
        await coordinator.CurrentRun!;
    }

    [Fact]
    public async Task Failing_stack_does_not_stop_later_stacks_and_marks_run_failed()
    {
        var updateService = new ScriptedUpdateService();
        updateService.Outcomes["database"] = StackOutcome.Failed;
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend"), Compose("database"), Compose("frontend")), updateService);

        await RunToCompletion(coordinator);

        var status = coordinator.GetStatus();
        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Equal(["backend", "database", "frontend"], status.Results.Select(r => r.Name).ToArray());
        Assert.Equal(["backend", "database", "frontend"], updateService.Updated);
        Assert.Equal(new UpdateSummary(2, 1, 0, 0, status.Summary!.Duration), status.Summary);
    }

    [Fact]
    public async Task Summary_counts_stacks_with_recreated_containers()
    {
        var updateService = new ScriptedUpdateService();
        updateService.RecreatedContainers["backend"] = 2;
        updateService.RecreatedContainers["webapp"] = 1;
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend"), Compose("proxy"), Compose("webapp")), updateService);

        await RunToCompletion(coordinator);

        Assert.Equal(2, coordinator.GetStatus().Summary!.Recreated);
        Assert.Contains(coordinator.GetLog(0), e => e.Text.StartsWith("Recreated:  2"));
    }

    [Fact]
    public async Task Exception_thrown_by_update_service_is_isolated_to_that_stack()
    {
        var updateService = new ScriptedUpdateService { ThrowFor = "database" };
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend"), Compose("database"), Compose("frontend")), updateService);

        await RunToCompletion(coordinator);

        var status = coordinator.GetStatus();
        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Equal(StackOutcome.Failed, status.Results.Single(r => r.Name == "database").Outcome);
        Assert.Equal(StackOutcome.Success, status.Results.Single(r => r.Name == "frontend").Outcome);
    }

    [Fact]
    public async Task Run_finishes_successfully_when_no_stack_fails()
    {
        var updateService = new ScriptedUpdateService();
        updateService.Outcomes["stopped"] = StackOutcome.Skipped;
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend"), Compose("stopped")), updateService);

        await RunToCompletion(coordinator);

        var status = coordinator.GetStatus();
        Assert.Equal(UpdateState.Finished, status.State);
        Assert.NotNull(status.FinishedAt);
        Assert.Null(status.CurrentStack);
        Assert.Equal(1, status.Summary!.Successful);
        Assert.Equal(1, status.Summary.Skipped);
    }

    [Fact]
    public async Task Log_contains_app_messages_and_process_output_with_increasing_sequence()
    {
        var updateService = new ScriptedUpdateService { OutputLine = "Container backend Started" };
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend")), updateService);

        await RunToCompletion(coordinator);

        var log = coordinator.GetLog(afterSequence: 0);
        Assert.Contains(log, e => e.Source == LogSource.App && e.Text.Contains("=== backend ==="));
        Assert.Contains(log, e => e.Source == LogSource.Process && e.Text == "Container backend Started");
        Assert.Equal(log.Select(e => e.Sequence).OrderBy(s => s), log.Select(e => e.Sequence));
        Assert.Equal(log.Count, log.Select(e => e.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task Stack_headings_show_position_and_total()
    {
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend"), Compose("frontend"), Compose("webapp")), new ScriptedUpdateService());

        await RunToCompletion(coordinator);

        var headings = coordinator.GetLog(0).Where(e => e.Text.StartsWith("=== ")).Select(e => e.Text).ToArray();
        Assert.Equal(["=== backend === [1 of 3]", "=== frontend === [2 of 3]", "=== webapp === [3 of 3]"], headings);
    }

    [Fact]
    public async Task GetLog_returns_only_entries_after_the_given_sequence()
    {
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend")), new ScriptedUpdateService());

        await RunToCompletion(coordinator);

        var all = coordinator.GetLog(0);
        var tail = coordinator.GetLog(all[1].Sequence);

        Assert.Equal(all.Skip(2), tail);
    }

    [Fact]
    public async Task In_memory_log_is_bounded_to_configured_line_count()
    {
        var stacks = Enumerable.Range(0, 50).Select(i => Compose($"stack{i:00}")).ToArray();
        var coordinator = CreateCoordinator(new FixedDiscovery(stacks), new ScriptedUpdateService(), maxLogLines: 20);

        await RunToCompletion(coordinator);

        var log = coordinator.GetLog(0);
        Assert.Equal(20, log.Count);
        Assert.Contains(log, e => e.Text.Contains("stack49"));
    }

    [Fact]
    public async Task Missing_root_directory_fails_the_run_instead_of_crashing()
    {
        var coordinator = CreateCoordinator(new ThrowingDiscovery(), new ScriptedUpdateService());

        await RunToCompletion(coordinator);

        var status = coordinator.GetStatus();
        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Contains(coordinator.GetLog(0), e => e.Text.Contains("does-not-exist"));
    }

    [Fact]
    public async Task Subscriber_receives_replay_then_live_entries_without_gaps_or_duplicates()
    {
        var gate = new TaskCompletionSource();
        var updateService = new ScriptedUpdateService { BeforeEachStack = () => gate.Task };
        var coordinator = CreateCoordinator(new FixedDiscovery(Compose("backend"), Compose("webapp")), updateService);

        Assert.True(coordinator.TryStartUpdate());
        using var subscription = coordinator.Subscribe(afterSequence: 0);
        gate.SetResult();
        await coordinator.CurrentRun!;

        var received = subscription.Replay.Select(e => e.Sequence).ToList();
        while (subscription.Reader.TryRead(out var evt))
        {
            if (evt is LogEntry entry)
            {
                received.Add(entry.Sequence);
            }
        }

        var expected = coordinator.GetLog(0).Select(e => e.Sequence).ToList();
        Assert.Equal(expected, received);
    }

    private sealed class FixedDiscovery(params DiscoveredStack[] stacks) : IStackDiscoveryService
    {
        public string RootDirectory => "/docker";
        public IReadOnlyList<DiscoveredStack> DiscoverStacks() => stacks;
    }

    private sealed class ThrowingDiscovery : IStackDiscoveryService
    {
        public string RootDirectory => "/does-not-exist";
        public IReadOnlyList<DiscoveredStack> DiscoverStacks() => throw new DirectoryNotFoundException("Root directory /does-not-exist was not found.");
    }

    private sealed class ScriptedUpdateService : IStackUpdateService
    {
        public Dictionary<string, StackOutcome> Outcomes { get; } = [];
        public Dictionary<string, int> RecreatedContainers { get; } = [];
        public List<string> Updated { get; } = [];
        public string? ThrowFor { get; init; }
        public string? OutputLine { get; init; }
        public Func<Task>? BeforeEachStack { get; init; }

        public async Task<StackResult> UpdateStackAsync(DiscoveredStack stack, IUpdateLog log, CancellationToken cancellationToken)
        {
            if (BeforeEachStack is not null)
            {
                await BeforeEachStack();
            }

            Updated.Add(stack.Name);
            if (stack.Name == ThrowFor)
            {
                throw new InvalidOperationException("boom");
            }

            if (OutputLine is not null)
            {
                log.Process(OutputLine);
            }

            var outcome = Outcomes.GetValueOrDefault(stack.Name, StackOutcome.Success);
            return new StackResult(stack.Name, outcome, TimeSpan.FromSeconds(1), outcome == StackOutcome.Success ? null : "scripted", RecreatedContainers.GetValueOrDefault(stack.Name));
        }
    }
}
