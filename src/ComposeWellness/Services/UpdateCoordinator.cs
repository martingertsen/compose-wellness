using System.Diagnostics;
using System.Threading.Channels;
using ComposeWellness.Configuration;
using ComposeWellness.Models;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Services;

/// <summary>
/// A live subscription to coordinator events. <see cref="Replay"/> holds the log entries that
/// existed when the subscription was created; <see cref="Reader"/> delivers everything after that.
/// Events are either <see cref="LogEntry"/> or <see cref="StatusSnapshot"/> instances.
/// </summary>
public sealed class Subscription(StatusSnapshot status, IReadOnlyList<LogEntry> replay, ChannelReader<object> reader, Action dispose) : IDisposable
{
    public StatusSnapshot Status { get; } = status;
    public IReadOnlyList<LogEntry> Replay { get; } = replay;
    public ChannelReader<object> Reader { get; } = reader;
    public void Dispose() => dispose();
}

/// <summary>
/// Owns the single global update operation: starts it, tracks its state, keeps the bounded
/// in-memory log and fans out events to connected browsers. Registered as a singleton.
/// </summary>
public sealed class UpdateCoordinator(
    IStackDiscoveryService discovery,
    IStackUpdateService stackUpdateService,
    UpdateLogArchive archive,
    IOptions<ComposeWellnessOptions> options,
    ILogger<UpdateCoordinator> logger)
{
    // One lock guards all mutable state below. Log lines arrive from process output threads
    // while HTTP requests read status, so every access goes through it.
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _log = new();
    private readonly Dictionary<Guid, Channel<object>> _subscribers = [];
    private long _sequence;
    private Session? _session;
    private Task? _run;
    private CancellationTokenSource? _cancellation;
    private TextWriter? _archiveWriter;

    /// <summary>The task of the running or most recent update. Used by tests to await completion.</summary>
    internal Task? CurrentRun
    {
        get
        {
            lock (_gate)
            {
                return _run;
            }
        }
    }

    /// <summary>Starts an update unless one is already running. Returns false in that case.</summary>
    public bool TryStartUpdate()
    {
        lock (_gate)
        {
            if (_session is { State: UpdateState.Running })
            {
                return false;
            }

            var session = new Session(Guid.NewGuid(), DateTimeOffset.Now);
            _session = session;
            _log.Clear();
            _cancellation = new CancellationTokenSource();
            _archiveWriter = archive.Open(session.StartedAt);

            var token = _cancellation.Token;
            // Task.Run detaches the update from any HTTP request, so closing the browser cannot stop it.
            _run = Task.Run(() => RunAsync(session, token), CancellationToken.None);

            Broadcast(SnapshotLocked());
            return true;
        }
    }

    /// <summary>Cancels the running update, if any. Called when the host is shutting down.</summary>
    public void CancelRunningUpdate()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
        }
    }

    public StatusSnapshot GetStatus()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>Returns the retained log entries with a sequence number greater than <paramref name="afterSequence"/>.</summary>
    public IReadOnlyList<LogEntry> GetLog(long afterSequence)
    {
        lock (_gate)
        {
            return _log.Where(entry => entry.Sequence > afterSequence).ToList();
        }
    }

    /// <summary>
    /// Subscribes to live events. The replay and the live channel are captured under the same lock,
    /// so a reader that consumes the replay first and then the channel sees every entry exactly once.
    /// </summary>
    public Subscription Subscribe(long afterSequence)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
            _subscribers[id] = channel;

            var replay = _log.Where(entry => entry.Sequence > afterSequence).ToList();
            return new Subscription(SnapshotLocked(), replay, channel.Reader, () => Unsubscribe(id));
        }
    }

    private void Unsubscribe(Guid id)
    {
        lock (_gate)
        {
            if (_subscribers.Remove(id, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    private async Task RunAsync(Session session, CancellationToken cancellationToken)
    {
        var log = new SessionLog(this);
        var anyFailed = false;

        try
        {
            log.App("Starting Docker update");
            log.App($"Root directory: {discovery.RootDirectory}");

            var stacks = discovery.DiscoverStacks();
            log.App($"Found {stacks.Count} directories to inspect");

            for (var index = 0; index < stacks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stack = stacks[index];
                SetCurrentStack(session, stack.Name);
                log.App(string.Empty);
                log.App($"=== {stack.Name} === [{index + 1} of {stacks.Count}]");

                var result = await UpdateOneAsync(stack, log, cancellationToken);
                anyFailed |= result.Outcome == StackOutcome.Failed;

                AddResult(session, result);
                log.App(result.Message is null
                    ? $"{stack.Name}: {Describe(result.Outcome)}"
                    : $"{stack.Name}: {Describe(result.Outcome)} - {result.Message}");
            }

            Finish(session, anyFailed ? UpdateState.Failed : UpdateState.Finished, log);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.App("Update cancelled because the service is shutting down");
            Finish(session, UpdateState.Failed, log);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update run failed before all stacks could be processed.");
            log.App($"Update failed: {ex.Message}");
            Finish(session, UpdateState.Failed, log);
        }
    }

    private async Task<StackResult> UpdateOneAsync(DiscoveredStack stack, SessionLog log, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await stackUpdateService.UpdateStackAsync(stack, log, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Failure isolation: whatever went wrong with this stack, the next one must still run.
            logger.LogError(ex, "Unexpected error while updating stack {Stack}.", stack.Name);
            log.App($"Unexpected error: {ex.Message}");
            return new StackResult(stack.Name, StackOutcome.Failed, stopwatch.Elapsed, $"Unexpected error: {ex.Message}");
        }
    }

    private void Finish(Session session, UpdateState finalState, SessionLog log)
    {
        var summary = Summarize(session, DateTimeOffset.Now);
        log.App(string.Empty);
        log.App(finalState == UpdateState.Finished ? "Update complete" : "Update finished with failures");
        log.App($"Successful: {summary.Successful}");
        log.App($"Failed:     {summary.Failed}");
        log.App($"Skipped:    {summary.Skipped}");
        log.App($"Recreated:  {summary.Recreated} (stacks with recreated containers)");
        log.App($"Duration:   {Format(summary.Duration)}");

        lock (_gate)
        {
            session.FinishedAt = DateTimeOffset.Now;
            session.State = finalState;
            session.CurrentStack = null;
            _archiveWriter?.Dispose();
            _archiveWriter = null;
            _cancellation?.Dispose();
            _cancellation = null;
            Broadcast(SnapshotLocked());
        }

        archive.Prune();
    }

    private void SetCurrentStack(Session session, string name)
    {
        lock (_gate)
        {
            session.CurrentStack = name;
            Broadcast(SnapshotLocked());
        }
    }

    private void AddResult(Session session, StackResult result)
    {
        lock (_gate)
        {
            session.Results.Add(result);
            Broadcast(SnapshotLocked());
        }
    }

    private void Append(LogSource source, string text)
    {
        lock (_gate)
        {
            var entry = new LogEntry(++_sequence, DateTimeOffset.Now, source, text);
            _log.Enqueue(entry);
            while (_log.Count > Math.Max(1, options.Value.MaxLogLines))
            {
                _log.Dequeue();
            }

            WriteToArchive(entry);
            Broadcast(entry);
        }
    }

    private void WriteToArchive(LogEntry entry)
    {
        if (_archiveWriter is null)
        {
            return;
        }

        try
        {
            var tag = entry.Source == LogSource.App ? "app" : "out";
            _archiveWriter.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{tag}] {entry.Text}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.LogWarning(ex, "Writing to the update log file failed. Disk logging is disabled for this update.");
            _archiveWriter.Dispose();
            _archiveWriter = null;
        }
    }

    private void Broadcast(object @event)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(@event);
        }
    }

    private StatusSnapshot SnapshotLocked()
    {
        if (_session is null)
        {
            return new StatusSnapshot(UpdateState.Idle, null, null, null, null, [], null, _sequence);
        }

        var summary = _session.State == UpdateState.Running ? null : Summarize(_session, _session.FinishedAt ?? DateTimeOffset.Now);
        return new StatusSnapshot(
            _session.State,
            _session.Id,
            _session.StartedAt,
            _session.FinishedAt,
            _session.CurrentStack,
            _session.Results.ToList(),
            summary,
            _sequence);
    }

    private static UpdateSummary Summarize(Session session, DateTimeOffset end)
    {
        // Only the update task itself adds results, so reading them from that same task needs no lock.
        var results = session.Results;
        return new UpdateSummary(
            results.Count(r => r.Outcome == StackOutcome.Success),
            results.Count(r => r.Outcome == StackOutcome.Failed),
            results.Count(r => r.Outcome == StackOutcome.Skipped),
            results.Count(r => r.RecreatedContainers > 0),
            end - session.StartedAt);
    }

    private static string Describe(StackOutcome outcome) => outcome.ToString().ToUpperInvariant();

    private static string Format(TimeSpan duration) => duration.ToString(duration.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");

    private sealed class Session(Guid id, DateTimeOffset startedAt)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset? FinishedAt { get; set; }
        public UpdateState State { get; set; } = UpdateState.Running;
        public string? CurrentStack { get; set; }
        public List<StackResult> Results { get; } = [];
    }

    /// <summary>Adapter that lets the stack update service write into this coordinator's log.</summary>
    private sealed class SessionLog(UpdateCoordinator owner) : IUpdateLog
    {
        public void App(string text) => owner.Append(LogSource.App, text);
        public void Process(string text) => owner.Append(LogSource.Process, text);
    }
}
