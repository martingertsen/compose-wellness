namespace ComposeWellness.Models;

/// <summary>Final result for one stack within an update session.</summary>
/// <param name="Message">Human readable detail: the failure or skip reason, or what changed on success.</param>
/// <param name="RecreatedContainers">
/// Number of containers of the stack that got a new container id during the update, which is what
/// happens when a container is recreated. Zero when nothing changed or when it could not be determined.
/// </param>
public sealed record StackResult(string Name, StackOutcome Outcome, TimeSpan Duration, string? Message, int RecreatedContainers = 0);
