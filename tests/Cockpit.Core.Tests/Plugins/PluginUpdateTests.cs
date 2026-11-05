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

// A failed live update restores the approved version; only an explicit manifest flag requires a restart.
public class PluginUpdateTests : IDisposable
{
    private const string Id = "update-probe";

    // Released by the test once the 5.0.0 start was given up on, and by Dispose when the test failed before that.
    private readonly ManualResetEventSlim _wake = new(false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-plugin-update-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnUpdateThatFailsToActivate_BringsTheOldVersionBackFromItsCopy_UnderTheOldPin_AndNeverShowsTheTrackerAsGone()
    {
        var pluginsRoot = Path.Combine(_root, "plugins");
        var config = Path.Combine(_root, "cockpit.json");
        var installer = new PluginInstaller(pluginsRoot);
        var store = new PluginRegistrationStore(config);
        var bootstrap = new PluginBootstrap(pluginsRoot, config);
        var initial = await installer.InstallFromZipAsync(_Zip("0.9.0", "MZ-v0"), AbstractionsContract.Version);
        Assert.False(initial.Staged);
        await store.SaveAsync(Id, new PluginRegistration(Enabled: true, PinnedSha256: initial.Sha256 ?? throw new InvalidOperationException()));
        var discovered = (await bootstrap.DiscoverAsync(AbstractionsContract.Version)).Single();

        var trackers = new TrackerProviderRegistry();
        var diagnostics = new PluginDiagnostics();
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, diagnostics);
        var instances = new List<Probe>();
        ICockpitPlugin Activate(DiscoveredPlugin candidate)
        {
            // What the activator does before it loads: the copy the load, and a later rollback, come from.
            PluginShadow.GetOrPrepare(candidate);
            instances.Add(new Probe(candidate.Manifest.Version, trackers, () => manager.RuntimeFor(Id), _wake));
            return instances[^1];
        }

        manager.LoadAndConfigure([discovered], new ServiceCollection(), Activate);
        Assert.NotNull(manager.WouldRefuse(discovered));
        var changes = new List<int>();
        var admin = new PluginAdministration(
            bootstrap, store, installer, new PluginProvisioningService(Substitute.For<IPluginStoreClient>(), installer), Substitute.For<IPluginStoreClient>(),
            diagnostics, manager, trackers: trackers, startGrace: candidate => candidate.Manifest.Version == "5.0.0" ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(30));
        manager.StartUiPart = (candidate, _, _) => Task.FromResult<string?>(candidate.Manifest.Version == "3.0.0" ? "3.0.0 UI cannot start" : null);

        var disabledBeforeStart = await admin.SetEnabledAsync(Id, false, discovered.Sha256);
        Assert.True(disabledBeforeStart.RestartRequired);
        Assert.False(instances[0].Disposed);
        Assert.False((await store.LoadAllAsync())[Id].Enabled);
        var enabledBeforeStart = await admin.SetEnabledAsync(Id, true, discovered.Sha256);
        Assert.True(enabledBeforeStart.RestartRequired);
        Assert.True((await store.LoadAllAsync())[Id].Enabled);

        using var cancelledStartup = new CancellationTokenSource();
        var waiting = manager.WaitForInitializationAsync(cancelledStartup.Token);
        Assert.False(waiting.IsCompleted);
        await cancelledStartup.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var updating = admin.InstallFromZipAsync(_Zip("1.0.0", "MZ-v1"));
        Assert.False(updating.IsCompleted);
        Assert.False(instances[0].Disposed);
        Assert.Equal(initial.Sha256, await PluginClosureHash.OfInstalledFolderAsync(Path.Combine(pluginsRoot, Id)));
        manager.Initialize((_, _) => Substitute.For<ICockpitHost>(), () =>
        {
            Assert.False(manager.WaitForInitializationAsync().IsCompleted);
            Assert.NotNull(manager.WouldRefuse(discovered));
            Assert.False(updating.IsCompleted);
            Assert.False(instances[0].Disposed);
        });
        var v1 = await updating;
        Assert.True(v1.Live?.IsLive);
        Assert.Equal("1.0.0", Assert.Single(manager.Loaded).Manifest.Version);
        Assert.True(instances[0].Disposed);
        trackers.Changed += (_, _) => changes.Add(trackers.Providers.Count);

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
        Assert.Equal(2, manager.RetiredLoadContexts);

        // The folder is the old version again, so a restart loads what runs now instead of waiting for approval of the failed one.
        Assert.Equal(v1.Sha256, await PluginClosureHash.OfInstalledFolderAsync(Path.Combine(pluginsRoot, Id)));
        Assert.Equal("MZ-v1", await File.ReadAllTextAsync(Path.Combine(pluginsRoot, Id, "Plugin.dll")));
        Assert.Equal(PluginLoadDecision.Load, (await bootstrap.DiscoverAsync(AbstractionsContract.Version)).Single().Decision);
        Assert.Equal(["0.9.0", "1.0.0", "2.0.0", "1.0.0"], instances.Select(probe => probe.Version));
        Assert.Equal([true, true, true, false], instances.Select(probe => probe.Disposed));
        Assert.True(instances[^1].Initialized);

        // The tracker was withdrawn and registered again inside the update; nobody saw it gone.
        Assert.Equal([1], changes);
        Assert.Single(trackers.Providers);

        // A UI part that fails fails the update: the old version comes back, where it used to read "updated".
        var uiFails = await admin.InstallFromZipAsync(_Zip("3.0.0", "MZ-v3"));
        Assert.True(uiFails.Live!.RolledBack);
        Assert.Contains("3.0.0 UI cannot start", uiFails.Live.RestartReason);
        Assert.Equal(v1.Sha256, Assert.Single(manager.Loaded).Sha256);
        Assert.Equal(v1.Sha256, (await store.LoadAllAsync())[Id].PinnedSha256);

        // A start that hangs is abandoned: the old version is back at once, and when the hung one wakes up it is disposed
        // and never joins what runs, so no late 5.0.0 runs under the pin of 1.0.0.
        var hung = await admin.InstallFromZipAsync(_Zip("5.0.0", "MZ-v5"));
        Assert.True(hung.Live!.RolledBack);
        Assert.Equal(v1.Sha256, Assert.Single(manager.Loaded).Sha256);
        Assert.Equal(v1.Sha256, (await store.LoadAllAsync())[Id].PinnedSha256);
        Assert.Equal(v1.Sha256, await PluginClosureHash.OfInstalledFolderAsync(Path.Combine(pluginsRoot, Id)));
        _wake.Set();
        Assert.True(SpinWait.SpinUntil(() => instances.Single(probe => probe.Version == "5.0.0").Disposed, TimeSpan.FromSeconds(10)));
        Assert.Equal(v1.Sha256, Assert.Single(manager.Loaded).Sha256);

        var undeclared = await admin.InstallFromZipAsync(_Zip("6.0.0", "MZ-v6"));
        var refused = Assert.IsType<PluginLiveState>(undeclared.Live);
        Assert.True(refused.RolledBack);
        Assert.False(refused.RestartRequired);
        Assert.Equal(v1.Sha256, Assert.Single(manager.Loaded).Sha256);

        var consent = await admin.InstallFromZipAsync(_Zip("7.0.0", "MZ-v7"));
        Assert.True(consent.InPlace);
        Assert.False(Assert.IsType<PluginLiveState>(consent.Live).RestartRequired);
        Assert.Equal(v1.Sha256, (await store.LoadAllAsync())[Id].PinnedSha256);
        await bootstrap.ReconcileUpdatesAsync();
        Assert.Equal(v1.Sha256, (await store.LoadAllAsync())[Id].PinnedSha256);
        Assert.Equal(v1.Sha256, Assert.Single(manager.Loaded).Sha256);
        var approved = await admin.SetEnabledAsync(Id, true, consent.Sha256 ?? throw new InvalidOperationException());
        Assert.True(approved.IsLive);
        Assert.Equal(consent.Sha256, Assert.Single(manager.Loaded).Sha256);
        Assert.Equal(consent.Sha256, (await store.LoadAllAsync())[Id].PinnedSha256);

        // Bytes that only a restart can start: the old version keeps running, and files and pin stay on the new ones.
        var restart = await admin.InstallFromZipAsync(_Zip("4.0.0", "MZ-v4"));
        Assert.False(restart.Live!.IsLive);
        Assert.False(restart.Live.Failed);
        Assert.True(restart.Live.RestartRequired);
        Assert.Equal(consent.Sha256, Assert.Single(manager.Loaded).Sha256);
        Assert.Equal(restart.Sha256, (await store.LoadAllAsync())[Id].PinnedSha256);
        Assert.Equal(restart.Sha256, await PluginClosureHash.OfInstalledFolderAsync(Path.Combine(pluginsRoot, ".pending-updates", Id)));
    }

    private string _Zip(string version, string dll)
    {
        Directory.CreateDirectory(_root);
        var zipPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifest = $$"""{"id":"{{Id}}","name":"{{Id}}","version":"{{version}}","requiresRestart":{{(version == "4.0.0" ? "true" : "false")}},"capabilities":{{(version == "7.0.0" ? "[\"time.entries\"]" : "[]")}},"entryAssembly":"Plugin.dll","abstractionsVersion":{{AbstractionsContract.Version}},"sessionProviders":[{"id":"{{Id}}.provider","name":"Probe"}]}""";
        foreach (var (name, content) in new Dictionary<string, string> { ["plugin.json"] = manifest, ["Plugin.dll"] = dll })
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return zipPath;
    }

    public void Dispose()
    {
        _wake.Set();
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
    private sealed class Probe(string version, ITrackerProviderRegistry trackers, Func<PluginRuntime> runtime, ManualResetEventSlim wake) : ICockpitPlugin
    {
        public PluginMetadata Metadata { get; } = new(Id, Id, version, null, null);

        public string Version { get; } = version;

        public bool Initialized { get; private set; }

        public bool Disposed { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
            if (Version is "4.0.0" or "6.0.0")
            {
                services.AddSingleton(new object());
            }
        }

        public void Initialize(ICockpitHost host)
        {
            var tracker = Substitute.For<ITrackerProvider>();
            tracker.TrackerId.Returns("probe-tracker");
            trackers.Register(tracker);
            runtime().Add(() => trackers.Unregister(tracker));
            if (Version == "5.0.0")
            {
                wake.Wait();
            }

            if (Version == "2.0.0")
            {
                throw new InvalidOperationException("2.0.0 cannot start");
            }

            Initialized = true;
        }

        public void Dispose() => Disposed = true;
    }
}
