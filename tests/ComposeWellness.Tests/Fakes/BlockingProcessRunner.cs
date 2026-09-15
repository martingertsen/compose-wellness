using ComposeWellness.Services;

namespace ComposeWellness.Tests.Fakes;

/// <summary>
/// A process runner that never completes on its own, so a test can keep an update running long
/// enough to exercise the "already running" interlock. <see cref="Release"/> lets it finish, and
/// the cancellation token is honored so the coordinator can still cancel it on shutdown.
/// </summary>
public sealed class BlockingProcessRunner : IProcessRunner
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    public async Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        await _gate.Task.WaitAsync(cancellationToken);
        return new ProcessResult(0, "");
    }
}
