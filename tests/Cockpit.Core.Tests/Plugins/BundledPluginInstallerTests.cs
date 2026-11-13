using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Bundled updates preserve operator choices and never resurrect an uninstalled plugin.
/// </summary>
public class BundledPluginInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _bundled;
    private readonly string _plugins;
    private readonly FakeRegistrationStore _registrations = new();

    public BundledPluginInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        _bundled = Path.Combine(_root, "bundled-plugins");
        _plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_bundled);
        Directory.CreateDirectory(_plugins);
    }

    // Turning a plugin off is a decision. Shipping a build is not a reason to undo it.
    [Fact]
    public async Task BundledUpdates_UpgradeSeedsAndPreserveOperatorChoices()
    {
        _WriteInstalled("depot", "1.1.0");
        _registrations.Saved["depot"] = new PluginRegistration(Enabled: false, PinnedSha256: "pinned");
        _WriteBundled("depot", "1.2.0");
        _WriteInstalled("seed", "0.45.7");
        _WriteBundled("seed", "0.66.1");
        _registrations.Saved["seed"] = new(true, "legacy-pin");
        _registrations.Seeded.Add("seed");
        _WriteInstalled("chosen", "0.45.7");
        _WriteBundled("chosen", "0.66.1");
        _registrations.Saved["chosen"] = new(true, "chosen-pin", VersionChosen: true);
        _WriteInstalled("newer", "0.67.0");
        _WriteBundled("newer", "0.66.1");
        _registrations.Saved["newer"] = new(true, "newer-pin");
        _WritePlugin(Path.Combine(_bundled, "future"), "future", "0.66.1", minHostVersion: $"{HostVersionInfo.Current.Major}.{int.MaxValue}.0");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        Assert.Equal(["seed"], installed);
        Assert.Equal("0.66.1", _InstalledVersion("seed"));
        Assert.Equal("0.45.7", _InstalledVersion("chosen"));
        Assert.Equal("0.67.0", _InstalledVersion("newer"));
        Assert.DoesNotContain("future", _registrations.Seeded);
        Assert.Equal("1.1.0", _InstalledVersion("depot"));
        Assert.Equal(new PluginRegistration(Enabled: false, PinnedSha256: "pinned"), _registrations.Saved["depot"]);

        _WriteBundled("seed", "0.67.0");
        Assert.Equal(["seed"], await NewSut().InstallAsync(_bundled, _plugins));
        _WriteInstalled("seed", "0.67.1-user");
        _WriteBundled("seed", "0.68.0");
        Assert.Empty(await NewSut().InstallAsync(_bundled, _plugins));
        Assert.Equal("0.67.1-user", _InstalledVersion("seed"));
        _WriteBundled("future", "0.66.1");
        Assert.Equal(["future"], await NewSut().InstallAsync(_bundled, _plugins));
    }

    // The point of the seed ledger: seeding is a first-appearance event, not a "is it on disk" check. A plugin
    // the operator uninstalled (folder and registration gone) must not quietly reappear on the next start.
    [Fact]
    public async Task AnUninstalledBundledPlugin_IsNotReSeeded()
    {
        _WriteBundled("depot", "1.1.0");
        Assert.Equal(new[] { "depot" }, (await NewSut().InstallAsync(_bundled, _plugins)));

        // Simulate an uninstall: the folder and the registration go, but the seed ledger remembers it was here.
        Directory.Delete(Path.Combine(_plugins, "depot"), recursive: true);
        _registrations.Saved.Remove("depot");

        var second = await NewSut().InstallAsync(_bundled, _plugins);

        Assert.Empty(second);
        Assert.False(Directory.Exists(Path.Combine(_plugins, "depot")), "an uninstalled bundled plugin does not silently return");
    }

    private BundledPluginInstaller NewSut() => new(_registrations);

    private string _InstalledVersion(string id)
    {
        var json = File.ReadAllText(Path.Combine(_plugins, id, "plugin.json"));
        Assert.True(PluginManifest.TryParse(json, out var manifest, out _));
        return manifest is null ? throw new InvalidOperationException($"'{id}' has no readable manifest.") : manifest.Version;
    }

    /// <param name="assemblyContent">Stands in for the compiled bytes — pass one to make a rebuild of the same version.</param>
    private void _WriteBundled(string id, string version, string? assemblyContent = null, string? expectedMinVersion = null) =>
        _WritePlugin(Path.Combine(_bundled, id), id, version, assemblyContent, expectedMinVersion);

    private void _WriteInstalled(string id, string version) => _WritePlugin(Path.Combine(_plugins, id), id, version);

    private static void _WritePlugin(
        string folder,
        string id,
        string version,
        string? assemblyContent = null,
        string? expectedMinVersion = null,
        string? minHostVersion = null)
    {
        Directory.CreateDirectory(folder);
        var assembly = $"Cockpit.Plugin.{id}.dll";
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "{{version}}",
              "entryAssembly": "{{assembly}}",
              "entryType": "Cockpit.Plugin.{{id}}.Entry",
              "abstractionsVersion": 1,
              "minHostVersion": {{System.Text.Json.JsonSerializer.Serialize(minHostVersion)}},
              "expectedMinVersion": {{System.Text.Json.JsonSerializer.Serialize(expectedMinVersion)}}
            }
            """);
        File.WriteAllBytes(Path.Combine(folder, assembly), System.Text.Encoding.UTF8.GetBytes(assemblyContent ?? $"{id}-{version}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeRegistrationStore : IPluginRegistrationStore
    {
        public Dictionary<string, PluginRegistration> Saved { get; } = [];

        public Task<IReadOnlyDictionary<string, PluginRegistration>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(Saved);

        public Task SaveAsync(string folderId, PluginRegistration registration, CancellationToken cancellationToken = default)
        {
            Saved[folderId] = registration;
            return Task.CompletedTask;
        }

        public Task SaveMenuPreferenceAsync(string folderId, int menuOrder, bool hiddenInMenu, CancellationToken cancellationToken = default)
        {
            Saved[folderId] = Saved.TryGetValue(folderId, out var existing)
                ? existing with { MenuOrder = menuOrder, HiddenInMenu = hiddenInMenu }
                : new PluginRegistration(Enabled: true, PinnedSha256: string.Empty, menuOrder, hiddenInMenu);

            return Task.CompletedTask;
        }

        public Task RemoveAsync(string folderId, CancellationToken cancellationToken = default)
        {
            Saved.Remove(folderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> LoadDataAsync(string folderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SaveDataAsync(string folderId, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public HashSet<string> Seeded { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlySet<string>> LoadSeededBundledIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(Seeded);

        public Task MarkBundledSeededAsync(IEnumerable<string> folderIds, CancellationToken cancellationToken = default)
        {
            foreach (var id in folderIds)
            {
                Seeded.Add(id);
            }

            return Task.CompletedTask;
        }
    }
}
