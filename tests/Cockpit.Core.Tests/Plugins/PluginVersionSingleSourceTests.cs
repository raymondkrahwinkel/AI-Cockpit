using System.Text.RegularExpressions;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// A plugin states its version in <c>plugin.json</c> and nowhere else (AC-301). The host reads the manifest
/// everywhere it shows or compares a version — the plugin manager row, the update check, the store — so a second
/// copy in <c>PluginMetadata</c> was a claim nothing verified, and eleven of twenty had drifted away from the
/// manifest by the time anyone compared them (GitHubPullRequests claimed a 2.1.1 that never existed).
/// <para>
/// Reading the source rather than the compiled plugins is deliberate: only a handful of plugins are built as a
/// test dependency, and the drift this guards against is written in C#, one plugin at a time.
/// </para>
/// </summary>
public partial class PluginVersionSingleSourceTests
{
    [Fact]
    public void EveryPlugin_StatesItsVersionInItsManifestOnly()
    {
        var plugins = _DiscoverPluginSources().ToList();
        Assert.True(System.Linq.Enumerable.Count(plugins) > 15);

        foreach (var (name, version, entrySourcePath) in plugins)
        {
            Assert.False(string.IsNullOrWhiteSpace(version));

            var metadata = _MetadataInitializer(File.ReadAllText(entrySourcePath));
            Assert.NotEmpty(metadata);

            Assert.False(MetadataVersionRegex().IsMatch(metadata),
                $"{name} declares a version in {Path.GetFileName(entrySourcePath)} as well as in plugin.json — " +
                "two sources drift, and the host only ever reads the manifest");
        }
    }

    // Every plugin folder whose manifest parses, paired with the source file of the class that manifest names as its
    // entry type. A folder whose entry source cannot be found by that name is skipped rather than failed — the count
    // assertion above is what catches a walk that stopped finding plugins altogether.
    private static IEnumerable<(string Name, string Version, string EntrySourcePath)> _DiscoverPluginSources()
    {
        var pluginsDev = _LocatePluginsDev()
            ?? throw new InvalidOperationException("No plugins-dev directory above the test output — these tests read the repo they belong to.");

        foreach (var folder in Directory.EnumerateDirectories(pluginsDev))
        {
            var manifestPath = Path.Combine(folder, "plugin.json");
            if (!File.Exists(manifestPath)
                || !PluginManifest.TryParse(File.ReadAllText(manifestPath), out var manifest, out _)
                || manifest?.EntryType is not { Length: > 0 } entryType)
            {
                continue;
            }

            var entrySourcePath = Path.Combine(folder, $"{entryType.Split('.').Last()}.cs");
            if (File.Exists(entrySourcePath))
            {
                yield return (Path.GetFileName(folder), manifest.Version, entrySourcePath);
            }
        }
    }

    private static string? _LocatePluginsDev()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plugins-dev");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    // Two ways a version reaches PluginMetadata from C#: named (Version: "1.2.3") or positional, where it is just the
    // third argument. The second is caught by the literal itself — a quoted bare "1.2"/"1.2.3", which an id, name,
    // author or description never is. Both are looked for inside the metadata initializer alone, so a version string
    // that legitimately belongs to the plugin's own logic elsewhere in the file is not mistaken for this.
    [GeneratedRegex(@"Version\s*:|""\d+\.\d+(\.\d+)?""", RegexOptions.Multiline)]
    private static partial Regex MetadataVersionRegex();

    // The Metadata initializer, from "new(" to the closing paren of that call — the only place this rule is about.
    private static string _MetadataInitializer(string source)
    {
        var start = source.IndexOf("Metadata { get; } = new(", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = source.IndexOf(");", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}
