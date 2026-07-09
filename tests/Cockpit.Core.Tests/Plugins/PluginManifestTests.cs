using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Parsing/validation of a plugin's <c>plugin.json</c> before anything is loaded (#14).</summary>
public class PluginManifestTests
{
    private const string Valid = """
        {
          "id": "github-issues",
          "name": "GitHub Issues",
          "version": "1.0.0",
          "entryAssembly": "Cockpit.Plugin.GitHubIssues.dll",
          "abstractionsVersion": 1,
          "entryType": "Cockpit.Plugin.GitHubIssues.Plugin",
          "minHostVersion": "12.0.0",
          "description": "Show open issues",
          "author": "Raymond"
        }
        """;

    [Fact]
    public void TryParse_ValidManifest_ParsesAllFields()
    {
        var parsed = PluginManifest.TryParse(Valid, out var manifest, out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        manifest.Should().NotBeNull();
        manifest!.Id.Should().Be("github-issues");
        manifest.Name.Should().Be("GitHub Issues");
        manifest.Version.Should().Be("1.0.0");
        manifest.EntryAssembly.Should().Be("Cockpit.Plugin.GitHubIssues.dll");
        manifest.AbstractionsVersion.Should().Be(1);
        manifest.EntryType.Should().Be("Cockpit.Plugin.GitHubIssues.Plugin");
        manifest.Description.Should().Be("Show open issues");
        manifest.Author.Should().Be("Raymond");
    }

    [Fact]
    public void TryParse_OnlyRequiredFields_LeavesOptionalsNull()
    {
        var json = """{"id":"x","name":"X","version":"1.0.0","entryAssembly":"X.dll","abstractionsVersion":1}""";

        PluginManifest.TryParse(json, out var manifest, out _).Should().BeTrue();
        manifest!.EntryType.Should().BeNull();
        manifest.MinHostVersion.Should().BeNull();
        manifest.Description.Should().BeNull();
        manifest.Author.Should().BeNull();
    }

    [Fact]
    public void TryParse_MissingRequiredField_FailsWithError()
    {
        var json = """{"id":"x","name":"X","version":"1.0.0","abstractionsVersion":1}""";

        PluginManifest.TryParse(json, out var manifest, out var error).Should().BeFalse();
        manifest.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void TryParse_MissingAbstractionsVersion_Fails()
    {
        var json = """{"id":"x","name":"X","version":"1.0.0","entryAssembly":"X.dll"}""";

        PluginManifest.TryParse(json, out _, out var error).Should().BeFalse();
        error.Should().Contain("abstractionsVersion");
    }

    [Fact]
    public void TryParse_InvalidJson_FailsWithoutThrowing()
    {
        PluginManifest.TryParse("{ not json", out var manifest, out var error).Should().BeFalse();
        manifest.Should().BeNull();
        error.Should().StartWith("Invalid JSON");
    }
}
