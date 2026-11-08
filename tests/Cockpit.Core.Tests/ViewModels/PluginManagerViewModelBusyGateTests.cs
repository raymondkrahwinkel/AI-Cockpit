using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// What the store may not let the operator do while it is working (AC-420). "Restart the cockpit now" is offered
/// by "Update all" after the *first* plugin of a batch, and pressing it there left plugins 2..n silently
/// un-updated behind a banner saying the update was done. Alongside it, three routes could each start a second
/// install on top of a running one — the version picker, Install from zip, and any catalogue install started
/// while a different command held the store.
/// </summary>
/// <remarks>
/// A button that starts its own command again is not among them: <c>AsyncRelayCommand</c> refuses to re-enter
/// itself, measured. What was missing is gating <em>across</em> commands, and a busy signal that a nested
/// operation could not clear while an outer one was still running.
/// </remarks>
public class PluginManagerViewModelBusyGateTests
{

    /// <summary>
    /// Remove a plugin, change your mind, install it again: the removal is applied at the next start, so the
    /// installer stages over the folder — which used to read "the state it had" off a registration Remove had just
    /// deleted, bringing the plugin back disabled with the new bytes pinned as approved. It must write no
    /// registration at all (awaiting-approval). An ordinary update over a known, enabled install still comes back
    /// enabled with the new bytes pinned and no consent prompt.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUpdate_KeepsAKnownInstallsEnabledState_ButAJustRemovedOneComesBackUnasked(bool knownInstall)
    {
        var registrations = new Dictionary<string, PluginRegistration>();
        if (knownInstall)
        {
            registrations["plugin-folder"] = new(Enabled: true, PinnedSha256: "sha256-of-the-old-bytes");
        }

        var registrationStore = Substitute.For<IPluginRegistrationStore>();
        registrationStore
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(registrations));
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        _Downloads(storeClient, () => { });
        _StagesTheUpdate(installer);
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>(), registrationStore: registrationStore);

        await manager.InstallFromStoreCommand.ExecuteAsync(_UpdatableRow("github-issues", "GitHub Issues"));

        if (knownInstall)
        {
            await registrationStore.Received(1).SaveAsync(
                "plugin-folder",
                Arg.Is<PluginRegistration>(saved => saved.Enabled && saved.PinnedSha256 == "sha256-of-the-new-bytes"),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await registrationStore.DidNotReceive().SaveAsync(
                Arg.Any<string>(), Arg.Any<PluginRegistration>(), Arg.Any<CancellationToken>());
            Assert.False(manager.NeedsRestart);
        }
    }

    private static readonly string _ZipPath = Path.Combine(Path.GetTempPath(), "ac-420-download-that-is-never-written.zip");

    private static void _Downloads(IPluginStoreClient storeClient, Action observe) =>
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<PluginDownloadProgress>?>())
            .Returns(_ =>
            {
                observe();
                return Task.FromResult(new PluginStoreDownloadResult(true, null, _ZipPath));
            });

    // Staged, which is what an update over an existing install is: it re-pins the new hash and skips rediscovery,
    // so the run never reaches PluginBootstrap and never touches the real plugins folder on disk.
    private static void _StagesTheUpdate(IPluginInstaller installer) =>
        installer
            .InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => Task.FromResult(PluginInstallResult.Success("plugin-folder", "sha256-of-the-new-bytes", staged: true)));

    private static PluginManagerViewModel _Manager(
        IPluginStoreClient storeClient,
        IPluginInstaller installer,
        IAppRestartService restartService,
        ISessionDialogService? dialogService = null,
        IPluginRegistrationStore? registrationStore = null)
    {
        // Only stubbed when this made it: a caller passing its own has already said what it holds, and
        // overwriting that here would quietly empty it.
        if (registrationStore is null)
        {
            registrationStore = Substitute.For<IPluginRegistrationStore>();
            registrationStore
                .LoadAllAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(new Dictionary<string, PluginRegistration>()));
        }

        var stateRoot = Path.Combine(Path.GetTempPath(), "cockpit-busy-gate-" + Guid.NewGuid().ToString("N"));
        return new PluginManagerViewModel(
            new PluginAdministration(new PluginBootstrap(Path.Combine(stateRoot, "plugins"), Path.Combine(stateRoot, "cockpit.json")), registrationStore, installer, new PluginProvisioningService(storeClient, installer, new PluginSignatureVerifier(), Substitute.For<IPluginTrustStore>()), storeClient),
            registrationStore,
            dialogService ?? Substitute.For<ISessionDialogService>(),
            Substitute.For<IPluginStoreConfigStore>(),
            storeClient,
            new Dictionary<string, PluginSettingsRegistration>(),
            restartService: restartService);
    }

    private static StorePluginRowViewModel _UpdatableRow(string id, string name)
    {
        var version = new PluginStoreVersion("2.0.0", $"plugins/{id}-2.0.0.zip", null, null, null, null);
        var entry = new PluginStoreEntry(id, name, null, "Cockpit", "2.0.0", [version]);

        return new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.example/index.json"), installedVersion: "1.0.0");
    }
}
