using ComposeWellness.Services;

namespace ComposeWellness.Tests;

/// <summary>
/// Runs real processes. The dotnet host is the one executable guaranteed to exist wherever the tests run.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static readonly string WorkingDirectory = Path.GetTempPath();

    [Fact]
    public async Task Captures_exit_code_and_streams_stdout_lines()
    {
        var lines = new List<string>();

        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("dotnet", ["--version"], WorkingDirectory),
            lines.Add,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(lines);
        Assert.Equal(string.Join(Environment.NewLine, lines) + Environment.NewLine, result.StandardOutput);
    }

    [Fact]
    public async Task Reports_non_zero_exit_code_and_streams_stderr()
    {
        var lines = new List<string>();

        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("dotnet", ["this-command-does-not-exist"], WorkingDirectory),
            lines.Add,
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public async Task Missing_executable_throws_process_start_exception()
    {
        var runner = new ProcessRunner();

        var ex = await Assert.ThrowsAsync<ProcessStartException>(() => runner.RunAsync(
            new ProcessRequest("compose-wellness-no-such-executable", [], WorkingDirectory),
            null,
            CancellationToken.None));

        Assert.Contains("compose-wellness-no-such-executable", ex.Message);
    }
}
