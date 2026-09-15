namespace ComposeWellness.Models;

/// <summary>Point-in-time view of the coordinator, returned by /api/status and sent over SSE.</summary>
public sealed record StatusSnapshot(
    UpdateState State,
    Guid? SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CurrentStack,
    IReadOnlyList<StackResult> Results,
    UpdateSummary? Summary,
    long LastSequence);
