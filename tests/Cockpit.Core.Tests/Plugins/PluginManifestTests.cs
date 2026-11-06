using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Parsing/validation of a plugin's <c>plugin.json</c> before anything is loaded (#14).</summary>
public class PluginManifestTests
{
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
