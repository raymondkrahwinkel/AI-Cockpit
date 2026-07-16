using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The shared install rule behind both the bundled installer and the dev sync. The bundled caller installs new
/// plugins (they ship); the dev caller only refreshes what is already installed (<c>installNew: false</c>) — but
/// both replace an existing install whose built bytes changed, which is the whole point: an install made the old
/// way is brought up to the new bytes rather than left hanging.
/// </summary>
public class PluginSourceInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _plugins;
    private readonly FakeRegistrationStore _registrations = new();

    public PluginSourceInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source");
        _plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_plugins);
    }

    // The migration Raymond asked for: an install made the old way is replaced by the freshly built bytes, even
    // though the version did not change (a rebuild has none to bump to), and its consent re-pins to the new bytes.
    [Fact]
    public async Task RefreshOnly_ReplacesAnInstalledPluginWhoseBuiltBytesChanged()
    {
        _WriteInstalled("codex", "1.0.0", assemblyContent: "old-bytes");
        _registrations.Saved["codex"] = new PluginRegistration(Enabled: true, PinnedSha256: "old-pin");
        var source = _WriteSource("codex", "1.0.0", assemblyContent: "rebuilt-bytes");

        var installed = await new PluginSourceInstaller(_registrations, null)
            .InstallFromSourceFoldersAsync([source], _plugins, installNew: false);

        installed.Should().Equal("codex");
        _InstalledAssembly("codex").Should().Be("rebuilt-bytes");
        _registrations.Saved["codex"].PinnedSha256.Should().NotBe("old-pin");
    }

    // Refresh-only is the looseness guarantee: a build never decides, on the operator's behalf, to install a
    // first-party plugin they never chose.
    [Fact]
    public async Task RefreshOnly_DoesNotInstallAPluginThatIsNotAlreadyInstalled()
    {
        var source = _WriteSource("codex", "1.0.0");

        var installed = await new PluginSourceInstaller(_registrations, null)
            .InstallFromSourceFoldersAsync([source], _plugins, installNew: false);

        installed.Should().BeEmpty();
        Directory.Exists(Path.Combine(_plugins, "codex")).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshOnly_LeavesADisabledPluginAlone()
    {
        _WriteInstalled("codex", "1.0.0", assemblyContent: "old-bytes");
        _registrations.Saved["codex"] = new PluginRegistration(Enabled: false, PinnedSha256: "pinned");
        var source = _WriteSource("codex", "1.0.0", assemblyContent: "rebuilt-bytes");

        var installed = await new PluginSourceInstaller(_registrations, null)
            .InstallFromSourceFoldersAsync([source], _plugins, installNew: false);

        installed.Should().BeEmpty();
        _InstalledAssembly("codex").Should().Be("old-bytes");
        _registrations.Saved["codex"].Should().Be(new PluginRegistration(Enabled: false, PinnedSha256: "pinned"));
    }

    // The bundled caller's side of the same routine: a plugin that ships is installed even when not there yet.
    [Fact]
    public async Task InstallNew_InstallsAPluginThatIsNotYetInstalled()
    {
        var source = _WriteSource("clock", "1.0.0");

        var installed = await new PluginSourceInstaller(_registrations, null)
            .InstallFromSourceFoldersAsync([source], _plugins, installNew: true);

        installed.Should().Equal("clock");
        File.Exists(Path.Combine(_plugins, "clock", "plugin.json")).Should().BeTrue();
        _registrations.Saved["clock"].Enabled.Should().BeTrue();
    }

    // A stray Cockpit.Plugins.Abstractions.dll in a source folder (a test project's output offered one, which is
    // how the dev sync first poisoned the plugin folders) must never be copied in: the shared contract loads from
    // the host, and a second copy gives the plugin's ICockpitPlugin its own identity and the loader rejects it.
    [Fact]
    public async Task Copy_NeverCarriesTheSharedAbstractionsAssemblyIntoAPluginFolder()
    {
        var source = _WriteSource("clock", "1.0.0");
        await File.WriteAllTextAsync(Path.Combine(source, "Cockpit.Plugins.Abstractions.dll"), "shared-contract");

        await new PluginSourceInstaller(_registrations, null)
            .InstallFromSourceFoldersAsync([source], _plugins, installNew: true);

        File.Exists(Path.Combine(_plugins, "clock", "Cockpit.Plugin.clock.dll")).Should().BeTrue();
        File.Exists(Path.Combine(_plugins, "clock", "Cockpit.Plugins.Abstractions.dll")).Should().BeFalse();
    }

    private string _InstalledAssembly(string id) =>
        File.ReadAllText(Path.Combine(_plugins, id, $"Cockpit.Plugin.{id}.dll"));

    private string _WriteSource(string id, string version, string? assemblyContent = null)
    {
        var folder = Path.Combine(_source, id);
        _WritePlugin(folder, id, version, assemblyContent);
        return folder;
    }

    private void _WriteInstalled(string id, string version, string? assemblyContent = null) =>
        _WritePlugin(Path.Combine(_plugins, id), id, version, assemblyContent);

    private static void _WritePlugin(string folder, string id, string version, string? assemblyContent)
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
              "abstractionsVersion": 1
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
    }
}
