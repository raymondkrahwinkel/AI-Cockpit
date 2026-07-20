using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The plugins that ship with the app: they are seeded into the operator's plugins directory on their first
/// appearance and pre-approved (they came out of the very build that would otherwise ask whether you trust
/// them), and after that first seed the bundle never touches them again — a bundled plugin is an ordinary,
/// store-updatable plugin that merely comes pre-installed. So a newer bundled version does not replace an
/// installed one, a rebuild does not re-pin it, a version the operator updated is not rolled back, one they
/// disabled stays disabled, and one they uninstalled does not silently return.
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
        _registrations.Seeded.Should().Contain("transcript-search", "a seeded plugin is recorded so it is never seeded again");
    }

    // The store owns every version after the first seed: a newer bundled build arriving in a later app version
    // must not overwrite what the operator is running, or a store update would be silently undone each start.
    [Fact]
    public async Task ANewerBundledVersion_DoesNotReplaceTheInstalledOne()
    {
        _WriteInstalled("transcript-search", "1.0.0");
        _registrations.Saved["transcript-search"] = new PluginRegistration(Enabled: true, PinnedSha256: "old");
        _WriteBundled("transcript-search", "1.1.0");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().BeEmpty();
        _InstalledVersion("transcript-search").Should().Be("1.0.0");
        _registrations.Saved["transcript-search"].PinnedSha256.Should().Be("old");
        _registrations.Seeded.Should().Contain("transcript-search", "an existing install is adopted into the ledger");
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

    /// <summary>
    /// Once seeded, the bundle keeps its hands off — a rebuilt bundled assembly does not overwrite the installed
    /// one or move its pin. Refreshing an already-installed first-party plugin from freshly built bytes is the
    /// dev inner loop's job (DevPluginInstaller, DEBUG only), not the bundle's: the bundle only ever seeds.
    /// </summary>
    [Fact]
    public async Task ARebuiltBundledPlugin_IsLeftAlone_AndNotRePinned()
    {
        _WriteInstalled("clock", "1.0.0");
        _registrations.Saved["clock"] = new PluginRegistration(Enabled: true, PinnedSha256: "the-old-bytes");
        _WriteBundled("clock", "1.0.0", assemblyContent: "clock-rebuilt");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().BeEmpty();
        _InstalledAssembly("clock").Should().Be("clock-1.0.0", "the bundle does not overwrite an install it already seeded");
        _registrations.Saved["clock"].PinnedSha256.Should().Be("the-old-bytes");
        _registrations.Seeded.Should().Contain("clock");
    }

    /// <summary>An identical, already-installed plugin is adopted into the ledger but its bytes and pin are untouched.</summary>
    [Fact]
    public async Task AnIdenticalBundledPlugin_IsLeftWhereItIs()
    {
        _WriteInstalled("clock", "1.0.0");
        _registrations.Saved["clock"] = new PluginRegistration(Enabled: true, PinnedSha256: "pinned");
        _WriteBundled("clock", "1.0.0");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().BeEmpty();
        _registrations.Saved["clock"].PinnedSha256.Should().Be("pinned");
    }

    [Fact]
    public async Task RunningTwice_InstallsNothingTheSecondTime()
    {
        _WriteBundled("transcript-search", "1.1.0");
        await NewSut().InstallAsync(_bundled, _plugins);

        var second = await NewSut().InstallAsync(_bundled, _plugins);

        second.Should().BeEmpty();
    }

    // The point of the seed ledger: seeding is a first-appearance event, not a "is it on disk" check. A plugin
    // the operator uninstalled (folder and registration gone) must not quietly reappear on the next start.
    [Fact]
    public async Task AnUninstalledBundledPlugin_IsNotReSeeded()
    {
        _WriteBundled("transcript-search", "1.1.0");
        (await NewSut().InstallAsync(_bundled, _plugins)).Should().Equal("transcript-search");

        // Simulate an uninstall: the folder and the registration go, but the seed ledger remembers it was here.
        Directory.Delete(Path.Combine(_plugins, "transcript-search"), recursive: true);
        _registrations.Saved.Remove("transcript-search");

        var second = await NewSut().InstallAsync(_bundled, _plugins);

        second.Should().BeEmpty();
        Directory.Exists(Path.Combine(_plugins, "transcript-search")).Should().BeFalse("an uninstalled bundled plugin does not silently return");
    }

    // Existing installs from before the ledger existed (or ones the operator installed themselves) are adopted:
    // recorded as seeded so the bundle never later overwrites them, without their bytes being rewritten now.
    [Fact]
    public async Task AnExistingInstall_IsAdoptedIntoTheLedger_WithoutRewriting()
    {
        _WriteInstalled("git-status", "1.0.0");
        _registrations.Saved["git-status"] = new PluginRegistration(Enabled: true, PinnedSha256: "theirs");
        _WriteBundled("git-status", "1.0.0", assemblyContent: "git-status-bundled");

        var installed = await NewSut().InstallAsync(_bundled, _plugins);

        installed.Should().BeEmpty();
        _InstalledAssembly("git-status").Should().Be("git-status-1.0.0", "adoption records the seed but does not rewrite the install");
        _registrations.Saved["git-status"].PinnedSha256.Should().Be("theirs");
        _registrations.Seeded.Should().Contain("git-status");
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

    private string _InstalledAssembly(string id) =>
        File.ReadAllText(Path.Combine(_plugins, id, $"Cockpit.Plugin.{id}.dll"));

    /// <param name="assemblyContent">Stands in for the compiled bytes — pass one to make a rebuild of the same version.</param>
    private void _WriteBundled(string id, string version, string? assemblyContent = null) =>
        _WritePlugin(Path.Combine(_bundled, id), id, version, assemblyContent);

    private void _WriteInstalled(string id, string version) => _WritePlugin(Path.Combine(_plugins, id), id, version);

    private static void _WritePlugin(string folder, string id, string version, string? assemblyContent = null)
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
