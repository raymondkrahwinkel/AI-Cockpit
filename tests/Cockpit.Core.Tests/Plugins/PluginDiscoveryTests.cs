using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Filesystem discovery of plugin folders (#14): parse manifest, hash entry assembly, decide — skipping non-plugin folders.</summary>
public class PluginDiscoveryTests : IDisposable
{
    private const int HostMajor = 1;
    private readonly string _root;

    public PluginDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-plugin-discovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task DiscoverAsync_ValidFolder_NeverSeen_DecidesNeedsConsent()
    {
        WritePlugin("github-issues", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        Assert.Single(found);
        Assert.Equal("github-issues", found[0].FolderId);
        Assert.Equal("github-issues-name", found[0].Manifest.Name);
        Assert.Equal(PluginLoadDecision.NeedsConsent, found[0].Decision);
        Assert.Matches("^[0-9a-f]{64}$", found[0].Sha256);
    }

    [Fact]
    public async Task DiscoverAsync_EnabledButADependencyDllWasSwappedKeepingSizeAndWriteTime_DecidesNeedsConsent()
    {
        // AC-43: the pin covers the whole closure, so a swapped dependency DLL re-triggers consent. AC-2018: Options
        // reuses an unchanged file's hash, and a swap that keeps size and write time must still be seen by the startup
        // pass — the only one whose decision loads anything — even with the pin taken from what Options showed.
        var folder = WritePlugin("weather", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        var dependency = Path.Combine(folder, "Dependency.dll");
        var writtenAt = DateTime.UtcNow.AddHours(-1);
        await File.WriteAllTextAsync(dependency, "original-dependency-bytes");
        File.SetLastWriteTimeUtc(dependency, writtenAt);
        var shown = await new PluginDiscovery().DiscoverAsync(_root, new Dictionary<string, PluginRegistration>(), HostMajor, forDisplay: true);
        var saved = new Dictionary<string, PluginRegistration> { ["weather"] = new(Enabled: true, PinnedSha256: Assert.Single(shown).Sha256) };

        await File.WriteAllTextAsync(dependency, "tampered-dependency-bytes");
        File.SetLastWriteTimeUtc(dependency, writtenAt);

        var found = await new PluginDiscovery().DiscoverAsync(_root, saved, HostMajor);

        Assert.Equal(PluginLoadDecision.NeedsConsent, Assert.Single(found).Decision);
    }

    private string WritePlugin(string folderId, string entryAssembly, int abstractionsVersion)
    {
        var folder = Path.Combine(_root, folderId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"),
            $$"""
            {"id":"{{folderId}}","name":"{{folderId}}-name","version":"1.0.0","entryAssembly":"{{entryAssembly}}","abstractionsVersion":{{abstractionsVersion}}}
            """);
        File.WriteAllText(Path.Combine(folder, entryAssembly), $"fake-assembly-bytes-for-{folderId}");
        return folder;
    }

    private static readonly IReadOnlyDictionary<string, PluginRegistration> Empty =
        new Dictionary<string, PluginRegistration>();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
