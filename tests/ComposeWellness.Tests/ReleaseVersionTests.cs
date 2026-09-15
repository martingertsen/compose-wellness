using ComposeWellness.Configuration;
using ComposeWellness.Services;

namespace ComposeWellness.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("V10.0.3", "10.0.3")]
    public void Parses_semantic_version_tags_with_or_without_v_prefix(string tag, string expected)
    {
        Assert.True(ReleaseVersion.TryParse(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.2.0-rc1")]
    [InlineData("release-1")]
    [InlineData("v1.2")]
    [InlineData("v1.2.0.4")]
    public void Rejects_tags_that_are_not_three_part_versions(string? tag)
    {
        Assert.False(ReleaseVersion.TryParse(tag, out _));
    }

    [Fact]
    public void Application_version_is_a_three_part_version()
    {
        var version = ApplicationVersion.FromAssembly();

        Assert.Matches(@"^\d+\.\d+\.\d+$", version.Text);
        Assert.Equal(Version.Parse(version.Text), version.Value);
    }

    [Theory]
    [InlineData("martingertsen/compose-wellness", true)]
    [InlineData("some.user/my_fork-2", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("nouser", false)]
    [InlineData("a/b/c", false)]
    [InlineData("https://github.com/a/b", false)]
    [InlineData("a b/c", false)]
    [InlineData("owner/repo\n", false)]
    public void Repository_must_be_empty_or_owner_slash_repo(string? repository, bool valid)
    {
        Assert.Equal(valid, ComposeWellnessOptions.IsValidRepository(repository));
    }
}
