using System.Text.Json;
using System.Text.Json.Serialization;
using ComposeWellness.Configuration;
using ComposeWellness.Models;
using ComposeWellness.Services;
using ComposeWellness.Web;

// The root self-update helper runs "ComposeWellness --version" to learn the installed version
// without talking to the web service, which may be stopped at that point.
var applicationVersion = ApplicationVersion.FromAssembly();
if (args.Contains("--version"))
{
    Console.WriteLine(applicationVersion.Text);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Listen on localhost only unless ASPNETCORE_URLS, a "Urls" setting or --Urls says otherwise.
// The default is deliberately not in appsettings.json: a value there would take precedence over
// the ASPNETCORE_URLS environment variable that the systemd drop-in uses.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5000");
}

builder.Services.AddOptions<ComposeWellnessOptions>()
    .Bind(builder.Configuration.GetSection(ComposeWellnessOptions.SectionName))
    .Validate(o => Path.IsPathRooted(o.RootDirectory), "ComposeWellness:RootDirectory must be an absolute path.")
    .Validate(o => o.MaxLogLines > 0, "ComposeWellness:MaxLogLines must be greater than zero.")
    .Validate(o => o.RetainedLogFiles >= 0, "ComposeWellness:RetainedLogFiles must not be negative.")
    .Validate(o => ComposeWellnessOptions.IsValidRepository(o.UpdateRepository), "ComposeWellness:UpdateRepository must be empty or \"owner/repo\".")
    .ValidateOnStart();

builder.Services.AddSingleton<IRootDirectoryProvider, RootDirectoryProvider>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IStackDiscoveryService, StackDiscoveryService>();
builder.Services.AddSingleton<IStackUpdateService, StackUpdateService>();
builder.Services.AddSingleton<UpdateLogArchive>();
builder.Services.AddSingleton<UpdateCoordinator>();

builder.Services.AddSingleton(applicationVersion);
builder.Services.AddSingleton<ISelfUpdateTrigger, SelfUpdateTrigger>();
// One instance serves both as the hosted service and as the read model for the API.
builder.Services.AddSingleton(sp => new ReleaseChecker(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ComposeWellnessOptions>>(),
    applicationVersion,
    new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
    sp.GetRequiredService<ILogger<ReleaseChecker>>()));
builder.Services.AddSingleton<IReleaseChecker>(sp => sp.GetRequiredService<ReleaseChecker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReleaseChecker>());

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

var coordinator = app.Services.GetRequiredService<UpdateCoordinator>();
var rootDirectory = app.Services.GetRequiredService<IRootDirectoryProvider>();

// A running update is cancelled on shutdown so child processes do not outlive the service.
app.Lifetime.ApplicationStopping.Register(coordinator.CancelRunningUpdate);

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Docker root directory: {RootDirectory}", rootDirectory.Current);
    if (!Directory.Exists(rootDirectory.Current))
    {
        app.Logger.LogWarning("Docker root directory {RootDirectory} does not exist. Updates will fail until it does.", rootDirectory.Current);
    }
});

app.UseDefaultFiles();
// After a self-update the page reloads itself; without this header the browser could keep the
// previous release's scripts and styles for days. ETags are still sent, so unchanged files cost
// one 304 round trip on a localhost connection.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

var api = app.MapGroup("/api");

// The custom header cannot be attached by a plain cross-site form post or a no-cors fetch, so
// requiring it on every state-changing request stops other websites open in the same browser
// from triggering actions (CSRF).
static bool HasRequestHeader(HttpRequest request) => request.Headers["X-Requested-With"] == "ComposeWellness";
static IResult MissingHeader() => Results.Json(new { error = "Missing X-Requested-With: ComposeWellness header." }, statusCode: StatusCodes.Status403Forbidden);

api.MapGet("/status", () => coordinator.GetStatus());

api.MapPost("/update", (HttpRequest request) =>
{
    if (!HasRequestHeader(request))
    {
        return MissingHeader();
    }

    return coordinator.TryStartUpdate()
        ? Results.Accepted("/api/status", coordinator.GetStatus())
        : Results.Conflict(new { error = "An update is already running." });
});

api.MapGet("/log", (long? after) => new { entries = coordinator.GetLog(after ?? 0) });

api.MapGet("/events", (HttpContext context, long? after, Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json) =>
    EventStream.WriteAsync(context, coordinator, after, json.Value.SerializerOptions));

// Preview of what "Update All" would process right now: directories with an update.sh or a
// Compose file. Whether a stack is stopped is only known once Docker is asked during the update.
api.MapGet("/stacks", (IStackDiscoveryService discovery) =>
{
    try
    {
        var stacks = discovery.DiscoverStacks()
            .Where(s => s.Kind != StackKind.None)
            .Select(s => new { s.Name, s.Kind, s.ComposeFile });
        return Results.Ok(new { rootDirectory = discovery.RootDirectory, stacks, error = (string?)null });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.Ok(new { rootDirectory = discovery.RootDirectory, stacks = Array.Empty<object>(), error = ex.Message });
    }
});

api.MapGet("/settings", (IReleaseChecker releases, ISelfUpdateTrigger selfUpdate) => new
{
    rootDirectory = rootDirectory.Current,
    configuredRootDirectory = rootDirectory.Configured,
    canChangeRootDirectory = rootDirectory.CanChange,
    version = applicationVersion.Text,
    updateRepository = releases.Repository,
    latestVersion = releases.LatestVersion?.ToString(),
    releaseUrl = releases.ReleaseUrl,
    updateAvailable = releases.UpdateAvailable,
    canSelfUpdate = selfUpdate.Enabled,
});

// Asks the root-owned helper unit to upgrade Compose Wellness to the latest release. The browser
// cannot choose what gets installed; it can only ask, and only when a newer release is known.
api.MapPost("/self-update", async (HttpRequest request, IReleaseChecker releases, ISelfUpdateTrigger selfUpdate, CancellationToken cancellationToken) =>
{
    if (!HasRequestHeader(request))
    {
        return MissingHeader();
    }

    if (!selfUpdate.Enabled)
    {
        return Results.Json(new { error = "Self-update is disabled." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (coordinator.GetStatus().State == UpdateState.Running)
    {
        return Results.Conflict(new { error = "Wait for the running update to finish." });
    }

    if (!releases.UpdateAvailable)
    {
        return Results.Conflict(new { error = "No newer version is known." });
    }

    try
    {
        await selfUpdate.RequestAsync(cancellationToken);
        app.Logger.LogInformation("Self-update to {Version} requested from the web UI.", releases.LatestVersion);
        return Results.Accepted((string?)null, new { latestVersion = releases.LatestVersion!.ToString() });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        app.Logger.LogError(ex, "Could not write the self-update trigger file.");
        return Results.Json(new { error = $"The update could not be requested: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

api.MapPut("/settings/root-directory", async (HttpRequest request, RootDirectoryChange change, CancellationToken cancellationToken) =>
{
    if (!HasRequestHeader(request))
    {
        return MissingHeader();
    }

    if (coordinator.GetStatus().State == UpdateState.Running)
    {
        return Results.Conflict(new { error = "The root directory cannot be changed while an update is running." });
    }

    try
    {
        await rootDirectory.SetAsync(change.RootDirectory ?? string.Empty, cancellationToken);
        return Results.Ok(new { rootDirectory = rootDirectory.Current });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Typically a data directory the service user cannot write to, for example a manual install
        // that did not set ComposeWellness:DataDirectory.
        app.Logger.LogError(ex, "Could not save settings.");
        return Results.Json(new { error = $"The new folder could not be saved: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

/// <summary>Request body for changing the root directory.</summary>
internal sealed record RootDirectoryChange(string? RootDirectory);
