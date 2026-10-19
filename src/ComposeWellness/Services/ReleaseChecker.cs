using System.Net.Http.Headers;
using System.Text.Json;
using ComposeWellness.Configuration;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Services;

/// <summary>
/// Asks the GitHub releases API for the latest release shortly after startup and every few hours.
/// Failures are expected on hosts without internet access, so they are logged quietly and the
/// previous result is kept.
/// </summary>
public sealed class ReleaseChecker : BackgroundService, IReleaseChecker
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly ApplicationVersion _running;
    private readonly HttpClient _http;
    private readonly ILogger<ReleaseChecker> _logger;
    private readonly object _gate = new();
    private Version? _latest;
    private string? _url;
    private string? _lastError;
    private readonly TimeProvider _time;
    private DateTimeOffset? _lastCheckedAt;
    private long? _lastCheckedTimestamp;
    private Task? _pendingCheck;
    private CancellationToken _stoppingToken = CancellationToken.None;

    public ReleaseChecker(IOptions<ComposeWellnessOptions> options, ApplicationVersion running, HttpClient http, ILogger<ReleaseChecker> logger, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Repository = options.Value.UpdateRepository ?? string.Empty;
        _running = running;
        _http = http;
        _logger = logger;

        // GitHub rejects requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ComposeWellness", running.Text));
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string Repository { get; }

    public Version? LatestVersion { get { lock (_gate) { return _latest; } } }

    public string? ReleaseUrl { get { lock (_gate) { return _url; } } }

    public bool UpdateAvailable { get { lock (_gate) { return _latest is not null && _latest > _running.Value; } } }

    public string? LastError { get { lock (_gate) { return _lastError; } } }

    public DateTimeOffset? LastCheckedAt { get { lock (_gate) { return _lastCheckedAt; } } }

    /// <summary>The check started by <see cref="RequestCheckIfStale"/>, so tests can await it.</summary>
    internal Task? PendingCheck { get { lock (_gate) { return _pendingCheck; } } }

    public bool RequestCheckIfStale(TimeSpan maxAge)
    {
        if (Repository.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_pendingCheck is { IsCompleted: false })
            {
                return true;
            }

            // Age is measured on the monotonic clock. The wall clock can jump backwards (for example
            // when NTP corrects the time shortly after a reboot), which would make the last check look
            // brand new for hours and hide a release published in between.
            if (_lastCheckedTimestamp is { } last && _time.GetElapsedTime(last) < maxAge)
            {
                return false;
            }

            // Opening the page shortly after a release should show the update without waiting
            // for the next scheduled check. One check per maxAge keeps a page reload storm far
            // below GitHub's unauthenticated rate limit.
            var token = _stoppingToken;
            _pendingCheck = Task.Run(async () =>
            {
                try
                {
                    await CheckAsync(token);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown while a check was running.
                }
            });
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        if (Repository.Length == 0)
        {
            _logger.LogInformation("Update check disabled: ComposeWellness:UpdateRepository is empty.");
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One check. Internal so tests can call it without waiting for the timer.</summary>
    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (Repository.Length == 0)
        {
            return;
        }

        try
        {
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Fail($"GitHub returned HTTP {(int)response.StatusCode} for {Repository}.");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var tag = json.RootElement.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var url = json.RootElement.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;

            if (!ReleaseVersion.TryParse(tag, out var version))
            {
                Fail($"The latest release of {Repository} has the tag '{tag}', which is not a version.");
                return;
            }

            lock (_gate)
            {
                _latest = version;
                _url = url;
                _lastError = null;
                MarkCheckedLocked();
            }

            _logger.LogInformation("Latest release of {Repository} is {Latest}; running {Running}.", Repository, version, _running.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A version check must never take the service down, so every other failure is recorded and retried next interval.
            Fail($"Could not check {Repository} for releases: {ex.Message}");
        }
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _lastError = message;
            // A failed check still counts as a check, otherwise an offline host would retry on every page load.
            MarkCheckedLocked();
        }

        // Logged every time: a failure that repeats silently is what makes a missing update notice
        // impossible to explain. Checks are rate limited, so this stays at a few lines per hour.
        _logger.LogWarning("Release check failed: {Message}", message);
    }

    private void MarkCheckedLocked()
    {
        _lastCheckedAt = _time.GetUtcNow();
        _lastCheckedTimestamp = _time.GetTimestamp();
    }
}
