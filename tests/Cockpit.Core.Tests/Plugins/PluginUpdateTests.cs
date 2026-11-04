using System.IO.Compression;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Tracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// AC-2025 (layer 2, isolation): a live update whose new version throws in Initialize puts the old version back from its
// copy: running, in its folder, and under its pin. The ticket allows two tests for AC-2025 (set before testregels v2); this is the second.
public class PluginUpdateTests : IDisposable
{
    private const string Id = "update-probe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-plugin-update-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnUpdateThatFailsToActivate_BringsTheOldVersionBackFromItsCopy_UnderTheOldPin_AndNeverShowsTheTrackerAsGone()
    {
        var pluginsRoot = Path.Combine(_root, "plugins");
        var config = Path.Combine(_root, "cockpit.json");
        var installer = new PluginInstaller(pluginsRoot);
        var store = new PluginRegistrationStore(config);
        var bootstrap = new PluginBootstrap(pluginsRoot, config);
        var v1 = await installer.InstallFromZipAsync(_Zip("1.0.0", "MZ-v1"), AbstractionsContract.Version);
        Assert.False(v1.Staged);
        await store.SaveAsync(Id, new PluginRegistration(Enabled: true, PinnedSha256: v1.Sha256!));
        var discovered = (await bootstrap.DiscoverAsync(AbstractionsContract.Version)).Single();

        var trackers = new TrackerProviderRegistry();
        var diagnostics = new PluginDiagnostics();
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, diagnostics);
        var instances = new List<Probe>();
        ICockpitPlugin Activate(DiscoveredPlugin candidate)
        {
            // What the activator does before it loads: the copy the load, and a later rollback, come from.
            PluginShadow.GetOrPrepare(candidate);
            instances.Add(new Probe(candidate.Manifest.Version, trackers, () => manager.RuntimeFor(Id)));
            return instances[^1];
        }

        manager.LoadAndConfigure([discovered], new ServiceCollection(), Activate);
        manager.Initialize((_, _) => Substitute.For<ICockpitHost>());
        var changes = new List<int>();
        trackers.Changed += (_, _) => changes.Add(trackers.Providers.Count);
        var admin = new PluginAdministration(
            bootstrap, store, installer, new PluginProvisioningService(Substitute.For<IPluginStoreClient>(), installer), Substitute.For<IPluginStoreClient>(),
            diagnostics, manager, trackers: trackers);

        var update = await admin.InstallFromZipAsync(_Zip("2.0.0", "MZ-v2"));

        // The install result carries the new bytes' hash, not the old (the pin must follow the bytes that take over).
        Assert.True(update.Staged);
        Assert.NotEqual(v1.Sha256, update.Sha256);
        Assert.Equal(["update-probe.provider"], update.Manifest!.SessionProviders.Select(provider => provider.Id));

        // The failure is shown, and the old version runs again, from its copy, under the pin it had.
        Assert.True(update.Live!.Failed);
        Assert.True(update.Live.RolledBack);
        Assert.Contains("Version 1.0.0 is running", update.Live.RestartReason);
        Assert.Equal(v1.Sha256, Assert.Single(manager.Loaded).Sha256);
        Assert.Equal((await store.LoadAllAsync())[Id].PinnedSha256, v1.Sha256);

        // The folder is the old version again, so a restart loads what runs now instead of waiting for approval of the failed one.
        Assert.Equal(v1.Sha256, await PluginClosureHash.OfInstalledFolderAsync(Path.Combine(pluginsRoot, Id)));
        Assert.Equal("MZ-v1", await File.ReadAllTextAsync(Path.Combine(pluginsRoot, Id, "Plugin.dll")));
        Assert.Equal(PluginLoadDecision.Load, (await bootstrap.DiscoverAsync(AbstractionsContract.Version)).Single().Decision);
        Assert.Equal(["1.0.0", "2.0.0", "1.0.0"], instances.Select(probe => probe.Version));
        Assert.Equal([true, true, false], instances.Select(probe => probe.Disposed));
        Assert.True(instances[^1].Initialized);

        // The tracker was withdrawn and registered again inside the update; nobody saw it gone.
        Assert.Equal([1], changes);
        Assert.Single(trackers.Providers);
    }

    private string _Zip(string version, string dll)
    {
        Directory.CreateDirectory(_root);
        var zipPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifest = $$"""{"id":"{{Id}}","name":"{{Id}}","version":"{{version}}","entryAssembly":"Plugin.dll","abstractionsVersion":{{AbstractionsContract.Version}},"sessionProviders":[{"id":"{{Id}}.provider","name":"Probe"}]}""";
        foreach (var (name, content) in new Dictionary<string, string> { ["plugin.json"] = manifest, ["Plugin.dll"] = dll })
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return zipPath;
    }

    public void Dispose()
    {
        // A copy a plugin loaded from stays open until the process ends; that is the point of loading from one.
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Registers a tracker, then fails when it is the 2.0.0 build: a half-initialised plugin the rollback has to clean up.
    private sealed class Probe(string version, ITrackerProviderRegistry trackers, Func<PluginRuntime> runtime) : ICockpitPlugin
    {
        public PluginMetadata Metadata { get; } = new(Id, Id, version, null, null);

        public string Version { get; } = version;

        public bool Initialized { get; private set; }

        public bool Disposed { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Initialize(ICockpitHost host)
        {
            var tracker = Substitute.For<ITrackerProvider>();
            tracker.TrackerId.Returns("probe-tracker");
            trackers.Register(tracker);
            runtime().Add(() => trackers.Unregister(tracker));
            if (Version == "2.0.0")
            {
                throw new InvalidOperationException("2.0.0 cannot start");
            }

            Initialized = true;
        }

        public void Dispose() => Disposed = true;
    }
}
