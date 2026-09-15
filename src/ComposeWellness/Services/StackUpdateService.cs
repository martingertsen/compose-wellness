using System.Diagnostics;
using ComposeWellness.Configuration;
using ComposeWellness.Models;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Services;

/// <summary>
/// Performs the update of one stack: either the custom update.sh or the standard Compose procedure.
/// </summary>
public sealed class StackUpdateService(IProcessRunner processRunner, IOptions<ComposeWellnessOptions> options) : IStackUpdateService
{
    // --ansi never keeps Docker Compose from emitting terminal control sequences, which would
    // otherwise show up as garbage in the browser console.
    private static readonly string[] ComposePrefix = ["compose", "--ansi", "never"];

    public async Task<StackResult> UpdateStackAsync(DiscoveredStack stack, IUpdateLog log, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (outcome, message, recreated) = stack.Kind switch
            {
                StackKind.CustomScript => await RunCustomScriptAsync(stack, log, cancellationToken),
                StackKind.Compose => await RunComposeUpdateAsync(stack, log, cancellationToken),
                _ => (StackOutcome.Skipped, "No update.sh or Compose file found", 0),
            };

            return new StackResult(stack.Name, outcome, stopwatch.Elapsed, message, recreated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProcessStartException ex)
        {
            log.App(ex.Message);
            return new StackResult(stack.Name, StackOutcome.Failed, stopwatch.Elapsed, ex.Message);
        }
    }

    private async Task<(StackOutcome, string?, int)> RunCustomScriptAsync(DiscoveredStack stack, IUpdateLog log, CancellationToken cancellationToken)
    {
        log.App("Custom update.sh detected, the script is responsible for the whole update");

        // When a Compose file sits next to the script, the same before/after comparison as for a
        // normal stack tells whether the script actually recreated anything.
        var before = stack.ComposeFile is null ? null : await TryListContainerIdsAsync(stack, cancellationToken);

        // "bash ./update.sh" works whether or not the script has its executable bit set.
        var result = await RunLoggedAsync(new ProcessRequest("bash", ["./" + StackDiscoveryService.UpdateScriptName], stack.Directory), log, cancellationToken);

        var after = before is null ? null : await TryListContainerIdsAsync(stack, cancellationToken);
        var change = DescribeChange(before, after);

        return result.ExitCode == 0
            ? (StackOutcome.Success, change.Message, change.Recreated)
            : (StackOutcome.Failed, $"update.sh exited with code {result.ExitCode}", change.Recreated);
    }

    private async Task<(StackOutcome, string?, int)> RunComposeUpdateAsync(DiscoveredStack stack, IUpdateLog log, CancellationToken cancellationToken)
    {
        log.App($"Compose stack detected ({stack.ComposeFile})");

        // The container id list is only interesting when the check fails, so it is not streamed live.
        var checkOutput = new List<string>();
        var check = await processRunner.RunAsync(
            new ProcessRequest("docker", ["compose", "ps", "-q", "--status", "running"], stack.Directory),
            checkOutput.Add,
            cancellationToken);

        if (check.ExitCode != 0)
        {
            checkOutput.ForEach(log.Process);
            return (StackOutcome.Failed, $"docker compose ps exited with code {check.ExitCode}", 0);
        }

        if (string.IsNullOrWhiteSpace(check.StandardOutput))
        {
            // A completely stopped stack was most likely stopped on purpose. Starting it again as a
            // side effect of an update would be surprising, so it is left alone.
            return (StackOutcome.Skipped, "Stack is not currently running", 0);
        }

        var before = await TryListContainerIdsAsync(stack, cancellationToken);

        var pull = await RunLoggedAsync(Compose(stack, "pull"), log, cancellationToken);
        if (pull.ExitCode != 0)
        {
            return (StackOutcome.Failed, $"docker compose pull exited with code {pull.ExitCode}", 0);
        }

        string[] upArguments = options.Value.RemoveOrphans ? ["up", "-d", "--remove-orphans"] : ["up", "-d"];
        var up = await RunLoggedAsync(Compose(stack, upArguments), log, cancellationToken);
        var after = await TryListContainerIdsAsync(stack, cancellationToken);
        var change = DescribeChange(before, after);

        if (up.ExitCode != 0)
        {
            return (StackOutcome.Failed, $"docker compose up exited with code {up.ExitCode}", change.Recreated);
        }

        return (StackOutcome.Success, change.Message, change.Recreated);
    }

    /// <summary>
    /// Lists the ids of all containers of the project, stopped ones included. Returns null when
    /// Compose cannot answer, so the caller degrades to "unknown" instead of failing the stack.
    /// </summary>
    private async Task<HashSet<string>?> TryListContainerIdsAsync(DiscoveredStack stack, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                new ProcessRequest("docker", ["compose", "ps", "-a", "-q"], stack.Directory),
                onOutputLine: null,
                cancellationToken);
        }
        catch (ProcessStartException)
        {
            // Docker itself is missing; the actual update command will report that properly.
            return null;
        }

        if (result.ExitCode != 0)
        {
            return null;
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A recreated container always gets a new id, so new ids after the update are recreations.</summary>
    private static (string? Message, int Recreated) DescribeChange(HashSet<string>? before, HashSet<string>? after)
    {
        if (before is null || after is null)
        {
            return (null, 0);
        }

        var recreated = after.Count(id => !before.Contains(id));
        if (recreated == 0)
        {
            return ("No change", 0);
        }

        var noun = after.Count == 1 ? "container" : "containers";
        return ($"Recreated {recreated} of {after.Count} {noun}", recreated);
    }

    private async Task<ProcessResult> RunLoggedAsync(ProcessRequest request, IUpdateLog log, CancellationToken cancellationToken)
    {
        log.App($"Running: {request.DisplayCommand}");
        return await processRunner.RunAsync(request, log.Process, cancellationToken);
    }

    private static ProcessRequest Compose(DiscoveredStack stack, params string[] arguments) =>
        new("docker", [.. ComposePrefix, .. arguments], stack.Directory);
}
