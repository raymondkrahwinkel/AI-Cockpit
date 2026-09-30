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

    private const string OnlyRequired = """{"id":"x","name":"X","version":"1.0.0","entryAssembly":"X.dll","abstractionsVersion":1}""";

    // AC-1389: a plugin that is only a UI part (a clock) names no backend assembly.
    private const string UiOnly = """{"id":"clock","name":"Clock","version":"1.0.0","uiAssembly":"Clock.dll","uiEntryType":"Clock.Ui","abstractionsVersion":2}""";

    // Every field, then only the required ones (optionals stay null), then a UI-only part.
    [Theory]
    [InlineData(Valid, "github-issues", "GitHub Issues", "1.0.0", "Cockpit.Plugin.GitHubIssues.dll", 1, "Cockpit.Plugin.GitHubIssues.Plugin", "12.0.0", "Show open issues", "Raymond", null, null)]
    [InlineData(OnlyRequired, "x", "X", "1.0.0", "X.dll", 1, null, null, null, null, null, null)]
    [InlineData(UiOnly, "clock", "Clock", "1.0.0", null, 2, null, null, null, null, "Clock.dll", "Clock.Ui")]
    public void TryParse_ReadsEveryFieldOfAManifest_AndLeavesTheOptionalOnesNull(
        string json, string id, string name, string version, string? entryAssembly, int abstractionsVersion,
        string? entryType, string? minHostVersion, string? description, string? author, string? uiAssembly, string? uiEntryType)
    {
        Assert.True(PluginManifest.TryParse(json, out var manifest, out var error), error);
        Assert.Equal(
            (id, name, version, entryAssembly, abstractionsVersion, entryType, minHostVersion, description, author, uiAssembly, uiEntryType),
            (manifest?.Id, manifest?.Name, manifest?.Version, manifest?.EntryAssembly, manifest?.AbstractionsVersion, manifest?.EntryType, manifest?.MinHostVersion, manifest?.Description, manifest?.Author, manifest?.UiAssembly, manifest?.UiEntryType));
    }

    [Theory]
    [InlineData("""{"id":"x","name":"X","version":"1.0.0","abstractionsVersion":1}""", "'entryAssembly' and 'uiAssembly'")]
    [InlineData("""{"id":"x","name":"X","version":"1.0.0","entryAssembly":"X.dll"}""", "abstractionsVersion")]
    [InlineData("{ not json", "Invalid JSON")]
    public void TryParse_ARefusedManifest_FailsWithItsReason_AndDoesNotThrow(string json, string expectedError)
    {
        Assert.False(PluginManifest.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Contains(expectedError, error);
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
}
