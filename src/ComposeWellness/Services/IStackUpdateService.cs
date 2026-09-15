using ComposeWellness.Models;

namespace ComposeWellness.Services;

public interface IStackUpdateService
{
    /// <summary>
    /// Updates a single stack and reports the outcome. Never throws for a failing stack;
    /// failures are returned as <see cref="StackOutcome.Failed"/> so the caller can continue.
    /// </summary>
    Task<StackResult> UpdateStackAsync(DiscoveredStack stack, IUpdateLog log, CancellationToken cancellationToken);
}
