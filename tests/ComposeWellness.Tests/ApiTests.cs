using System.Net;
using System.Net.Http.Json;
using ComposeWellness.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ComposeWellness.Tests;

/// <summary>Exercises the HTTP surface through the real pipeline against an empty root directory.</summary>
public sealed class ApiTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ComposeWellness:RootDirectory"] = _root.Path,
                ["ComposeWellness:LogDirectory"] = null,
                ["ComposeWellness:DataDirectory"] = _root.CreateDirectory("data"),
                ["ComposeWellness:AllowRootDirectoryChange"] = "true",
            })));
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

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }
}
