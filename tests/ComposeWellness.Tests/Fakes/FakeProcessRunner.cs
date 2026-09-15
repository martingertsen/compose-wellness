using ComposeWellness.Services;

namespace ComposeWellness.Tests.Fakes;

/// <summary>
/// Scripted process runner. Each registered handler matches on the display command
/// (for example "docker compose pull") and returns a canned result. Handlers are tried in
/// registration order, so register the more specific command text first.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(Func<ProcessRequest, bool> Match, Queue<ProcessResult> Results, string[] Output)> _handlers = [];

    public List<ProcessRequest> Requests { get; } = [];

    public FakeProcessRunner On(string commandContains, int exitCode, string stdout = "", params string[] outputLines)
    {
        _handlers.Add((Matcher(commandContains), new Queue<ProcessResult>([new ProcessResult(exitCode, stdout)]), outputLines));
        return this;
    }

    /// <summary>Returns the given stdout values one after another; the last one repeats.</summary>
    public FakeProcessRunner OnSequence(string commandContains, params string[] stdouts)
    {
        _handlers.Add((Matcher(commandContains), new Queue<ProcessResult>(stdouts.Select(s => new ProcessResult(0, s))), []));
        return this;
    }

    public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        foreach (var (match, results, output) in _handlers)
        {
            if (!match(request))
            {
                continue;
            }

            foreach (var line in output)
            {
                onOutputLine?.Invoke(line);
            }

            var result = results.Count > 1 ? results.Dequeue() : results.Peek();
            return Task.FromResult(result);
        }

        throw new InvalidOperationException($"No fake handler for: {request.DisplayCommand}");
    }

    private static Func<ProcessRequest, bool> Matcher(string commandContains) =>
        r => r.DisplayCommand.Contains(commandContains, StringComparison.Ordinal);
}
