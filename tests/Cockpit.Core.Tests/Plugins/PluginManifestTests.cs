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
        Assert.Contains("'entryAssembly' and 'uiAssembly'", error);
    }

    // AC-1389: a plugin that is only a UI part (a clock) names no backend assembly.
    [Fact]
    public void TryParse_UiAssemblyWithoutEntryAssembly_Parses()
    {
        var json = """{"id":"clock","name":"Clock","version":"1.0.0","uiAssembly":"Clock.dll","uiEntryType":"Clock.Ui","abstractionsVersion":2}""";

        Assert.True(PluginManifest.TryParse(json, out var manifest, out var error), error);
        Assert.Equal((null, "Clock.dll", "Clock.Ui"), (manifest?.EntryAssembly, manifest?.UiAssembly, manifest?.UiEntryType));
    }

    // AC-1389 counter-proof: the manifests in the repository still parse and name an assembly to load. Since AC-1390
    // a plugin may split in two or be only a UI part, so which of the two it names is no longer pinned here.
    [Theory]
    [MemberData(nameof(InRepoManifests))]
    public void TryParse_EveryInRepoManifest_StillParsesAndNamesAnAssembly(string manifestPath)
    {
        Assert.True(PluginManifest.TryParse(File.ReadAllText(manifestPath), out var manifest, out var error), error);
        Assert.NotEmpty(manifest?.Assemblies ?? []);
    }

    public static TheoryData<string> InRepoManifests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cockpit.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return new TheoryData<string>(Directory.GetFiles(Path.Combine(root.FullName, "plugins-dev"), "plugin.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
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
