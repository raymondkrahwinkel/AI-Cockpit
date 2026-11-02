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
    public async Task DiscoverAsync_EnabledWithMatchingHash_DecidesLoad()
    {
        var folder = WritePlugin("weather", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        var hash = await PluginClosureHash.OfInstalledFolderAsync(folder);
        var saved = new Dictionary<string, PluginRegistration> { ["weather"] = new(Enabled: true, PinnedSha256: hash) };
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, saved, HostMajor);

        Assert.Equal(PluginLoadDecision.Load, Assert.Single(found).Decision);
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

    [Fact]
    public async Task DiscoverAsync_EnabledWithALegacyEntryOnlyPin_ReConsentsOnceThenLoads()
    {
        // The AC-43 migration, exactly as an operator meets it: a plugin consented before this change carries a pin
        // over only the entry assembly's bytes (PluginHash.Compute of the entry). On the first launch after the
        // update the closure no longer matches, so a store-installed plugin re-prompts once...
        var folder = WritePlugin("weather", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        await File.WriteAllTextAsync(Path.Combine(folder, "Dependency.dll"), "dependency-bytes");
        var legacyEntryOnlyPin = PluginHash.Compute(await File.ReadAllBytesAsync(Path.Combine(folder, "Plugin.dll")));
        var beforeConsent = new Dictionary<string, PluginRegistration> { ["weather"] = new(Enabled: true, PinnedSha256: legacyEntryOnlyPin) };

        var found = await new PluginDiscovery().DiscoverAsync(_root, beforeConsent, HostMajor);
        Assert.Equal(PluginLoadDecision.NeedsConsent, Assert.Single(found).Decision);

        // ...and once re-consented (the pin becomes the closure), it loads and stays put on the next launch.
        var closurePin = await PluginClosureHash.OfInstalledFolderAsync(folder);
        var afterConsent = new Dictionary<string, PluginRegistration> { ["weather"] = new(Enabled: true, PinnedSha256: closurePin) };

        var reloaded = await new PluginDiscovery().DiscoverAsync(_root, afterConsent, HostMajor);
        Assert.Equal(PluginLoadDecision.Load, Assert.Single(reloaded).Decision);
    }

    [Fact]
    public async Task DiscoverAsync_AbstractionsMajorMismatch_DecidesMismatch()
    {
        WritePlugin("future", entryAssembly: "Plugin.dll", abstractionsVersion: 2);
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        Assert.Equal(PluginLoadDecision.AbstractionsMajorMismatch, Assert.Single(found).Decision);
    }

    [Fact]
    public async Task DiscoverAsync_SkipsFoldersWithoutManifest_BadManifest_OrMissingEntryAssembly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "no-manifest"));

        var bad = Path.Combine(_root, "bad-manifest");
        Directory.CreateDirectory(bad);
        await File.WriteAllTextAsync(Path.Combine(bad, "plugin.json"), "{ not json");

        var noEntry = Path.Combine(_root, "no-entry");
        Directory.CreateDirectory(noEntry);
        await File.WriteAllTextAsync(Path.Combine(noEntry, "plugin.json"),
            """{"id":"no-entry","name":"n","version":"1.0.0","entryAssembly":"Missing.dll","abstractionsVersion":1}""");

        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        Assert.Empty(found);
    }

    // AC-1159: the entryPath check that used to be a bare File.Exists let a `Path.Combine` with `..` walk
    // out of `folder` and hash/pick up a sibling's real, already-installed assembly instead.
    [Fact]
    public async Task DiscoverAsync_EntryAssemblyEscapesViaDotDot_SkipsIt()
    {
        Directory.CreateDirectory(Path.Combine(_root, "victim"));
        await File.WriteAllTextAsync(Path.Combine(_root, "victim", "plugin.json"),
            """{"id":"victim","name":"v","version":"1.0.0","entryAssembly":"../approved-plugin/entry.dll","abstractionsVersion":1}""");
        WritePlugin("approved-plugin", entryAssembly: "entry.dll", abstractionsVersion: 1);
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        Assert.Single(found);
        Assert.Equal("approved-plugin", found[0].FolderId);
    }

    [Fact]
    public async Task DiscoverAsync_EntryAssemblyIsRooted_SkipsIt()
    {
        var outside = Path.Combine(_root, "elsewhere.dll");
        await File.WriteAllTextAsync(outside, "elsewhere-bytes");
        Directory.CreateDirectory(Path.Combine(_root, "victim"));
        await File.WriteAllTextAsync(Path.Combine(_root, "victim", "plugin.json"),
            $$"""{"id":"victim","name":"v","version":"1.0.0","entryAssembly":"{{outside.Replace('\\', '/')}}","abstractionsVersion":1}""");
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        Assert.Empty(found);
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
