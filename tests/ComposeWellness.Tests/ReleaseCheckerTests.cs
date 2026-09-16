using System.Net;
using ComposeWellness.Configuration;
using ComposeWellness.Services;
using ComposeWellness.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Tests;

public sealed class ReleaseCheckerTests
{
    private static ReleaseChecker Create(string running, HttpStatusCode status, string body, out StubHttpMessageHandler handler, string repository = "owner/repo")
    {
        handler = new StubHttpMessageHandler(status, body);
        return new ReleaseChecker(
            Options.Create(new ComposeWellnessOptions { UpdateRepository = repository }),
            new ApplicationVersion(running),
            new HttpClient(handler),
            NullLogger<ReleaseChecker>.Instance);
    }

    [Fact]
    public async Task Newer_release_is_reported_as_an_available_update()
    {
        var checker = Create("1.0.0", HttpStatusCode.OK,
            """{ "tag_name": "v1.1.0", "html_url": "https://github.com/owner/repo/releases/tag/v1.1.0" }""", out var handler);

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(new Version(1, 1, 0), checker.LatestVersion);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.1.0", checker.ReleaseUrl);
        Assert.True(checker.UpdateAvailable);
        Assert.Null(checker.LastError);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", request.RequestUri!.ToString());
        Assert.Contains("ComposeWellness/1.0.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Same_or_older_release_is_not_an_update()
    {
        var checker = Create("1.1.0", HttpStatusCode.OK, """{ "tag_name": "v1.1.0", "html_url": "u" }""", out _);

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(new Version(1, 1, 0), checker.LatestVersion);
        Assert.False(checker.UpdateAvailable);
    }

    [Fact]
    public async Task Http_error_keeps_the_previous_result_and_records_the_error()
    {
        var checker = Create("1.0.0", HttpStatusCode.Forbidden, """{ "message": "rate limited" }""", out _);

        await checker.CheckAsync(CancellationToken.None);

        Assert.Null(checker.LatestVersion);
        Assert.False(checker.UpdateAvailable);
        Assert.Contains("403", checker.LastError);
    }

    [Fact]
    public async Task Unparseable_tag_is_an_error_not_an_update()
    {
        var checker = Create("1.0.0", HttpStatusCode.OK, """{ "tag_name": "nightly", "html_url": "u" }""", out _);

        await checker.CheckAsync(CancellationToken.None);

        Assert.False(checker.UpdateAvailable);
        Assert.Contains("nightly", checker.LastError);
    }

    [Fact]
    public async Task Later_http_error_keeps_the_previous_success_but_records_the_error()
    {
        var checker = Create("1.0.0", HttpStatusCode.OK,
            """{ "tag_name": "v1.1.0", "html_url": "https://github.com/owner/repo/releases/tag/v1.1.0" }""", out var handler);
        await checker.CheckAsync(CancellationToken.None);
        handler.QueueResponse(HttpStatusCode.InternalServerError, """{ "message": "server error" }""");

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(new Version(1, 1, 0), checker.LatestVersion);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.1.0", checker.ReleaseUrl);
        Assert.True(checker.UpdateAvailable);
        Assert.NotNull(checker.LastError);
    }

    [Fact]
    public async Task Empty_repository_disables_the_check()
    {
        var checker = Create("1.0.0", HttpStatusCode.OK, """{ "tag_name": "v9.9.9", "html_url": "u" }""", out var handler, repository: "");

        await checker.CheckAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.False(checker.UpdateAvailable);
    }

    [Fact]
    public async Task Stale_request_starts_a_check_and_a_fresh_result_does_not()
    {
        var checker = Create("1.0.0", HttpStatusCode.OK, """{ "tag_name": "v1.1.0", "html_url": "u" }""", out var handler);
        Assert.Null(checker.LastCheckedAt);

        Assert.True(checker.RequestCheckIfStale(TimeSpan.FromMinutes(10)));
        await checker.PendingCheck!;

        Assert.NotNull(checker.LastCheckedAt);
        Assert.True(checker.UpdateAvailable);
        Assert.Single(handler.Requests);

        Assert.False(checker.RequestCheckIfStale(TimeSpan.FromMinutes(10)));
        Assert.Single(handler.Requests);

        Assert.True(checker.RequestCheckIfStale(TimeSpan.Zero));
        await checker.PendingCheck!;
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Failed_check_still_counts_as_checked()
    {
        var checker = Create("1.0.0", HttpStatusCode.InternalServerError, """{ "message": "boom" }""", out var handler);

        Assert.True(checker.RequestCheckIfStale(TimeSpan.FromMinutes(10)));
        await checker.PendingCheck!;

        Assert.NotNull(checker.LastCheckedAt);
        Assert.NotNull(checker.LastError);
        Assert.False(checker.RequestCheckIfStale(TimeSpan.FromMinutes(10)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Disabled_checker_never_starts_a_check()
    {
        var checker = Create("1.0.0", HttpStatusCode.OK, """{ "tag_name": "v9.9.9", "html_url": "u" }""", out var handler, repository: "");

        Assert.False(checker.RequestCheckIfStale(TimeSpan.Zero));
        Assert.Null(checker.PendingCheck);
        Assert.Empty(handler.Requests);
    }
}
