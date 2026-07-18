using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

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
    public async Task DiscoverAsync_NoRoot_ReturnsEmpty()
    {
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(Path.Combine(_root, "does-not-exist"), Empty, HostMajor);

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_ValidFolder_NeverSeen_DecidesNeedsConsent()
    {
        WritePlugin("github-issues", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        found.Should().ContainSingle();
        found[0].FolderId.Should().Be("github-issues");
        found[0].Manifest.Name.Should().Be("github-issues-name");
        found[0].Decision.Should().Be(PluginLoadDecision.NeedsConsent);
        found[0].Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task DiscoverAsync_EnabledWithMatchingHash_DecidesLoad()
    {
        var folder = WritePlugin("weather", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        var hash = await PluginClosureHash.OfInstalledFolderAsync(folder);
        var saved = new Dictionary<string, PluginRegistration> { ["weather"] = new(Enabled: true, PinnedSha256: hash) };
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, saved, HostMajor);

        found.Should().ContainSingle().Which.Decision.Should().Be(PluginLoadDecision.Load);
    }

    [Fact]
    public async Task DiscoverAsync_EnabledButADependencyDllWasSwapped_DecidesNeedsConsent()
    {
        // The heart of AC-43: the pin used to cover only the entry assembly, so a swapped dependency DLL loaded
        // unconsented. The pin is now over the whole closure, so tampering with any sibling file re-triggers consent.
        var folder = WritePlugin("weather", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        await File.WriteAllTextAsync(Path.Combine(folder, "Dependency.dll"), "original-dependency-bytes");
        var pinned = await PluginClosureHash.OfInstalledFolderAsync(folder);
        var saved = new Dictionary<string, PluginRegistration> { ["weather"] = new(Enabled: true, PinnedSha256: pinned) };

        // Entry assembly untouched; only a dependency it loads is swapped.
        await File.WriteAllTextAsync(Path.Combine(folder, "Dependency.dll"), "tampered-dependency-bytes");

        var found = await new PluginDiscovery().DiscoverAsync(_root, saved, HostMajor);

        found.Should().ContainSingle().Which.Decision.Should().Be(PluginLoadDecision.NeedsConsent);
    }

    [Fact]
    public async Task DiscoverAsync_AbstractionsMajorMismatch_DecidesMismatch()
    {
        WritePlugin("future", entryAssembly: "Plugin.dll", abstractionsVersion: 2);
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        found.Should().ContainSingle().Which.Decision.Should().Be(PluginLoadDecision.AbstractionsMajorMismatch);
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

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_SkipsReservedDotPrefixedFolders_EvenWithAValidManifest()
    {
        // A leftover .staging-* extraction or the .pending-updates staging area carries a valid manifest but is
        // not an installed plugin, so it must never be discovered as a phantom duplicate.
        WritePlugin(".staging-abc123", entryAssembly: "Plugin.dll", abstractionsVersion: 1);
        var discovery = new PluginDiscovery();

        var found = await discovery.DiscoverAsync(_root, Empty, HostMajor);

        found.Should().BeEmpty();
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
