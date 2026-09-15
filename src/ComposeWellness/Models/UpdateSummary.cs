namespace ComposeWellness.Models;

/// <param name="Recreated">Number of stacks in which at least one container was recreated.</param>
public sealed record UpdateSummary(int Successful, int Failed, int Skipped, int Recreated, TimeSpan Duration);
