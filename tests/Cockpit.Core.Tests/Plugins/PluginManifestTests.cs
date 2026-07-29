using Cockpit.Core.Plugins;

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

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("github-issues", manifest!.Id);
        Assert.Equal("GitHub Issues", manifest.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("Cockpit.Plugin.GitHubIssues.dll", manifest.EntryAssembly);
        Assert.Equal(1, manifest.AbstractionsVersion);
        Assert.Equal("Cockpit.Plugin.GitHubIssues.Plugin", manifest.EntryType);
        Assert.Equal("Show open issues", manifest.Description);
        Assert.Equal("Raymond", manifest.Author);
    }

    [Fact]
    public void TryParse_OnlyRequiredFields_LeavesOptionalsNull()
    {
        var json = """{"id":"x","name":"X","version":"1.0.0","entryAssembly":"X.dll","abstractionsVersion":1}""";

        Assert.True(PluginManifest.TryParse(json, out var manifest, out _));
        Assert.Null(manifest!.EntryType);
        Assert.Null(manifest.MinHostVersion);
        Assert.Null(manifest.Description);
        Assert.Null(manifest.Author);
    }

    [Fact]
    public void TryParse_MissingRequiredField_FailsWithError()
    {
        var json = """{"id":"x","name":"X","version":"1.0.0","abstractionsVersion":1}""";

        Assert.False(PluginManifest.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_MissingAbstractionsVersion_Fails()
    {
        var json = """{"id":"x","name":"X","version":"1.0.0","entryAssembly":"X.dll"}""";

        Assert.False(PluginManifest.TryParse(json, out _, out var error));
        Assert.Contains("abstractionsVersion", error);
    }

    [Fact]
    public void TryParse_InvalidJson_FailsWithoutThrowing()
    {
        Assert.False(PluginManifest.TryParse("{ not json", out var manifest, out var error));
        Assert.Null(manifest);
        Assert.StartsWith("Invalid JSON", error);
    }
}
