using System.Net;
using System.Net.Http.Json;
using ComposeWellness.Services;
using ComposeWellness.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComposeWellness.Tests;

/// <summary>Exercises the HTTP surface through the real pipeline against an empty root directory.</summary>
public sealed class ApiTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly FakeReleaseChecker _releases = new();
    private readonly string _triggerFile;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
    {
        _triggerFile = Path.Combine(_root.CreateDirectory("state"), "self-update.request");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ComposeWellness:RootDirectory"] = _root.Path,
                ["ComposeWellness:LogDirectory"] = null,
                ["ComposeWellness:DataDirectory"] = _root.CreateDirectory("data"),
                ["ComposeWellness:AllowRootDirectoryChange"] = "true",
                // Never call GitHub from the tests; the fake below is what the endpoints see.
                ["ComposeWellness:UpdateRepository"] = "",
                ["ComposeWellness:SelfUpdateTriggerFile"] = _triggerFile,
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IReleaseChecker>(_releases));
        });
    }

    [Fact]
    public async Task Status_is_available_without_special_headers()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"state\":\"idle\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Static_files_are_served_with_a_no_cache_header()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Stacks_endpoint_lists_only_directories_the_update_will_process()
    {
        _root.CreateFile("webapp/compose.yml");
        _root.CreateFile("custom-app/update.sh");
        _root.CreateFile("backend/docker-compose.yaml");
        _root.CreateDirectory("misc-files");
        _root.CreateFile("_Archive/old/compose.yml");
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/stacks");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"name\":\"backend\",\"kind\":\"compose\",\"composeFile\":\"docker-compose.yaml\"", body);
        Assert.Contains("\"name\":\"custom-app\",\"kind\":\"customScript\"", body);
        Assert.Contains("\"name\":\"webapp\"", body);
        Assert.DoesNotContain("misc-files", body);
        Assert.DoesNotContain("_Archive", body);
        Assert.True(body.IndexOf("backend", StringComparison.Ordinal) < body.IndexOf("webapp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_the_root_directory_persists_and_changes_the_stack_list()
    {
        _root.CreateFile("webapp/compose.yml");
        var other = new TempDirectory();
        other.CreateFile("proxy/compose.yml");
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComposeWellness");

        var response = await client.PutAsJsonAsync("/api/settings/root-directory", new { rootDirectory = other.Path });
        var stacks = await client.GetStringAsync("/api/stacks");
        var settings = await client.GetStringAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("proxy", stacks);
        Assert.DoesNotContain("webapp", stacks);
        Assert.Contains("\"canChangeRootDirectory\":true", settings);
        Assert.True(File.Exists(Path.Combine(_root.Path, "data", "settings.json")));
        other.Dispose();
    }

    [Fact]
    public async Task Settings_report_the_application_version()
    {
        using var client = _factory.CreateClient();

        var settings = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/settings");

        var version = settings.GetProperty("version").GetString();
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }

    [Fact]
    public async Task Changing_the_root_directory_to_a_missing_path_is_a_bad_request()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComposeWellness");

        var response = await client.PutAsJsonAsync("/api/settings/root-directory", new { rootDirectory = Path.Combine(_root.Path, "nope") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Changing_the_root_directory_requires_the_request_header()
    {
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings/root-directory", new { rootDirectory = _root.Path });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_without_the_request_header_is_rejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/update", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Contains("\"state\":\"idle\"", await (await client.GetAsync("/api/status")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Update_with_the_request_header_is_accepted()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComposeWellness");

        var response = await client.PostAsync("/api/update", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Settings_report_update_information()
    {
        _releases.LatestVersion = new Version(9, 9, 9);
        _releases.ReleaseUrl = "https://example.test/release";
        _releases.UpdateAvailable = true;
        using var client = _factory.CreateClient();

        var settings = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/settings");

        Assert.Equal("owner/repo", settings.GetProperty("updateRepository").GetString());
        Assert.Equal("9.9.9", settings.GetProperty("latestVersion").GetString());
        Assert.Equal("https://example.test/release", settings.GetProperty("releaseUrl").GetString());
        Assert.True(settings.GetProperty("updateAvailable").GetBoolean());
        Assert.True(settings.GetProperty("canSelfUpdate").GetBoolean());
    }

    [Fact]
    public async Task Self_update_requires_the_request_header()
    {
        _releases.UpdateAvailable = true;
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/self-update", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(_triggerFile));
    }

    [Fact]
    public async Task Self_update_without_a_newer_version_is_a_conflict()
    {
        _releases.UpdateAvailable = false;
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComposeWellness");

        var response = await client.PostAsync("/api/self-update", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No newer version", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(_triggerFile));
    }

    [Fact]
    public async Task Self_update_writes_the_trigger_file()
    {
        _releases.LatestVersion = new Version(9, 9, 9);
        _releases.UpdateAvailable = true;
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComposeWellness");

        var response = await client.PostAsync("/api/self-update", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"latestVersion\":\"9.9.9\"", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(_triggerFile));
    }

    [Fact]
    public async Task Self_update_is_a_conflict_while_an_update_runs()
    {
        _root.CreateFile("webapp/compose.yml");
        _releases.UpdateAvailable = true;
        _releases.LatestVersion = new Version(9, 9, 9);
        var blockingRunner = new BlockingProcessRunner();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IProcessRunner>(blockingRunner)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComposeWellness");

        var updateResponse = await client.PostAsync("/api/update", content: null);
        Assert.Equal(HttpStatusCode.Accepted, updateResponse.StatusCode);

        // The update starts on a background task, so give it a short moment to actually reach the
        // "running" state before asserting the interlock against it.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        string status;
        do
        {
            status = await client.GetStringAsync("/api/status");
            if (status.Contains("\"state\":\"running\"", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);
        Assert.Contains("\"state\":\"running\"", status);

        var selfUpdateResponse = await client.PostAsync("/api/self-update", content: null);
        var body = await selfUpdateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, selfUpdateResponse.StatusCode);
        Assert.Contains("Wait for the running update", body);
        Assert.False(File.Exists(_triggerFile));

        // Let the blocked update finish so the host can shut down cleanly at the end of the test.
        blockingRunner.Release();
        var coordinator = factory.Services.GetRequiredService<UpdateCoordinator>();
        if (coordinator.CurrentRun is { } run)
        {
            await run;
        }
    }

    [Fact]
    public async Task Self_update_is_forbidden_when_disabled()
    {
        _releases.UpdateAvailable = true;
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ComposeWellness:AllowSelfUpdate"] = "false",
            })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "ComposeWellness");

        var response = await client.PostAsync("/api/self-update", content: null);
        var settings = await client.GetStringAsync("/api/settings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"canSelfUpdate\":false", settings);
        Assert.False(File.Exists(_triggerFile));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }
}
