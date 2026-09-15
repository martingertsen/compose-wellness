using ComposeWellness.Configuration;
using ComposeWellness.Models;
using ComposeWellness.Services;
using ComposeWellness.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ComposeWellness.Tests;

public sealed class StackDiscoveryServiceTests : IDisposable
{
    private readonly TempDirectory _root = new();

    private StackDiscoveryService CreateService() => CreateService(_root.Path);

    private static StackDiscoveryService CreateService(string rootDirectory) =>
        new(new RootDirectoryProvider(
            Options.Create(new ComposeWellnessOptions { RootDirectory = rootDirectory, DataDirectory = Path.GetTempPath() }),
            NullLogger<RootDirectoryProvider>.Instance));

    [Fact]
    public void Discovers_only_immediate_child_directories_sorted_by_name()
    {
        _root.CreateFile("webapp/compose.yml");
        _root.CreateFile("backend/compose.yml");
        _root.CreateFile("_Archive/backend/compose.yml");
        _root.CreateDirectory("misc-files");
        _root.CreateFile("loose-file.txt");

        var stacks = CreateService().DiscoverStacks();

        Assert.Equal(["_Archive", "backend", "misc-files", "webapp"], stacks.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Nested_compose_file_does_not_make_parent_a_compose_stack()
    {
        _root.CreateFile("_Archive/backend/compose.yml");

        var archive = Assert.Single(CreateService().DiscoverStacks());

        Assert.Equal(StackKind.None, archive.Kind);
        Assert.Null(archive.ComposeFile);
    }

    [Fact]
    public void Update_script_takes_precedence_over_compose_file()
    {
        _root.CreateFile("custom-app/compose.yml");
        _root.CreateFile("custom-app/update.sh", "#!/bin/bash\n");

        var stack = Assert.Single(CreateService().DiscoverStacks());

        Assert.Equal(StackKind.CustomScript, stack.Kind);
    }

    [Theory]
    [InlineData("compose.yml")]
    [InlineData("compose.yaml")]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.yaml")]
    public void Recognizes_supported_compose_file_names(string fileName)
    {
        _root.CreateFile($"proxy/{fileName}");

        var stack = Assert.Single(CreateService().DiscoverStacks());

        Assert.Equal(StackKind.Compose, stack.Kind);
        Assert.Equal(fileName, stack.ComposeFile);
    }

    [Fact]
    public void Directory_without_script_or_compose_file_is_kind_none()
    {
        _root.CreateDirectory("misc-files");

        var stack = Assert.Single(CreateService().DiscoverStacks());

        Assert.Equal(StackKind.None, stack.Kind);
    }

    [Fact]
    public void Stack_directory_is_absolute_path_inside_root()
    {
        _root.CreateFile("proxy/compose.yml");

        var stack = Assert.Single(CreateService().DiscoverStacks());

        Assert.True(Path.IsPathRooted(stack.Directory));
        Assert.Equal(Path.Combine(_root.Path, "proxy"), stack.Directory);
    }

    [Fact]
    public void Missing_root_directory_throws()
    {
        var service = CreateService(Path.Combine(_root.Path, "does-not-exist"));

        Assert.Throws<DirectoryNotFoundException>(() => service.DiscoverStacks());
    }

    public void Dispose() => _root.Dispose();
}
