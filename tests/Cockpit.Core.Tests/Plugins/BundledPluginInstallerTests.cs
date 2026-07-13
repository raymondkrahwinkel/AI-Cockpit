using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The plugins that ship with the app: they land in the operator's plugins directory on startup and are
/// pre-approved (they came out of the very build that would otherwise ask whether you trust them), a newer
/// bundled version replaces an older installed one — and none of that overrides a decision the operator made:
/// a plugin they turned off stays off, and a version they updated past ours is not rolled back.
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

    [Fact]
    public async Task AFreshCockpit_GetsTheBundledPlugin_EnabledWithoutAskingForConsent()
    {
        _WriteBundled("transcript-search", "1.1.0");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().Equal("transcript-search");
        File.Exists(Path.Combine(_plugins, "transcript-search", "plugin.json")).Should().BeTrue();
        _registrations.Saved["transcript-search"].Enabled.Should().BeTrue();
        _registrations.Saved["transcript-search"].PinnedSha256.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ANewerBundledVersion_ReplacesTheInstalledOne()
    {
        _WriteInstalled("transcript-search", "1.0.0");
        _registrations.Saved["transcript-search"] = new PluginRegistration(Enabled: true, PinnedSha256: "old");
        _WriteBundled("transcript-search", "1.1.0");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().Equal("transcript-search");
        _InstalledVersion("transcript-search").Should().Be("1.1.0");
        _registrations.Saved["transcript-search"].PinnedSha256.Should().NotBe("old");
    }

    // The operator updated it from the store past what we ship; a new app build must not drag them back.
    [Fact]
    public async Task AnInstalledVersionNewerThanOurs_IsLeftAlone()
    {
        _WriteInstalled("transcript-search", "2.0.0");
        _registrations.Saved["transcript-search"] = new PluginRegistration(Enabled: true, PinnedSha256: "theirs");
        _WriteBundled("transcript-search", "1.1.0");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().BeEmpty();
        _InstalledVersion("transcript-search").Should().Be("2.0.0");
        _registrations.Saved["transcript-search"].PinnedSha256.Should().Be("theirs");
    }

    // Turning a plugin off is a decision. Shipping a build is not a reason to undo it.
    [Fact]
    public async Task APluginTheOperatorDisabled_StaysDisabledAndUntouched()
    {
        _WriteInstalled("git-status", "1.1.0");
        _registrations.Saved["git-status"] = new PluginRegistration(Enabled: false, PinnedSha256: "pinned");
        _WriteBundled("git-status", "1.2.0");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().BeEmpty();
        _InstalledVersion("git-status").Should().Be("1.1.0");
        _registrations.Saved["git-status"].Should().Be(new PluginRegistration(Enabled: false, PinnedSha256: "pinned"));
    }

    [Fact]
    public async Task RunningTwice_InstallsNothingTheSecondTime()
    {
        _WriteBundled("transcript-search", "1.1.0");
        await NewSut().InstallAsync(_bundled, _plugins);

        var second = await NewSut().InstallAsync(_bundled, _plugins);

        second.Should().BeEmpty();
    }

    [Fact]
    public async Task NoBundledFolder_IsNotAnError()
    {
        var installed = await NewSut().InstallAsync(Path.Combine(_root, "does-not-exist"), _plugins);

        installed.Should().BeEmpty();
    }

    private BundledPluginInstaller NewSut() => new(_registrations);

    private string _InstalledVersion(string id)
    {
        var json = File.ReadAllText(Path.Combine(_plugins, id, "plugin.json"));
        PluginManifest.TryParse(json, out var manifest, out _).Should().BeTrue();
        return manifest is null ? throw new InvalidOperationException($"'{id}' has no readable manifest.") : manifest.Version;
    }

    private void _WriteBundled(string id, string version) => _WritePlugin(Path.Combine(_bundled, id), id, version);

    private void _WriteInstalled(string id, string version) => _WritePlugin(Path.Combine(_plugins, id), id, version);

    private static void _WritePlugin(string folder, string id, string version)
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
        File.WriteAllBytes(Path.Combine(folder, assembly), System.Text.Encoding.UTF8.GetBytes($"{id}-{version}"));
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
