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
    /// Remove a plugin, change your mind, install it again. The removal is applied at the next start, so the
    /// folder is still there and the installer stages over it — which used to walk into the update branch and
    /// read "the state it had" off a registration Remove had just deleted. That reads as disabled, so the
    /// plugin came back switched off and with the new bytes pinned as approved, under a line promising it would
    /// activate. It must write no registration at all: no registration is what awaiting-approval looks like.
    /// </summary>
    [Fact]
    public async Task Reinstalling_APluginYouJustRemoved_DoesNotComeBackDisabledAndUnasked()
    {
        var registrationStore = Substitute.For<IPluginRegistrationStore>();
        registrationStore
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(new Dictionary<string, PluginRegistration>()));
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        _Downloads(storeClient, () => { });
        _StagesTheUpdate(installer);
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>(), registrationStore: registrationStore);

        await manager.InstallFromStoreCommand.ExecuteAsync(_UpdatableRow("github-issues", "GitHub Issues"));

        await registrationStore.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<PluginRegistration>(), Arg.Any<CancellationToken>());
        Assert.True(manager.NeedsRestart);
    }

    /// <summary>
    /// And the ordinary update still keeps what it had, which is the branch above's whole reason for existing:
    /// an enabled plugin that updates comes back enabled, with the new bytes pinned, and no consent prompt.
    /// </summary>
    [Fact]
    public async Task AnUpdateOverAKnownInstall_KeepsItsEnabledStateAndRepinsTheNewBytes()
    {
        var registrationStore = Substitute.For<IPluginRegistrationStore>();
        registrationStore
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(
                new Dictionary<string, PluginRegistration>
                {
                    ["plugin-folder"] = new(Enabled: true, PinnedSha256: "sha256-of-the-old-bytes"),
                }));
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        _Downloads(storeClient, () => { });
        _StagesTheUpdate(installer);
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>(), registrationStore: registrationStore);

        await manager.InstallFromStoreCommand.ExecuteAsync(_UpdatableRow("github-issues", "GitHub Issues"));

        await registrationStore.Received(1).SaveAsync(
            "plugin-folder",
            Arg.Is<PluginRegistration>(saved => saved.Enabled && saved.PinnedSha256 == "sha256-of-the-new-bytes"),
            Arg.Any<CancellationToken>());
    }

    private static readonly string _ZipPath = Path.Combine(Path.GetTempPath(), "ac-420-download-that-is-never-written.zip");

    // An older version than the row advertises — what the detail panel's per-version install rolls back to.
    private static readonly PluginStoreVersion _RollbackVersion =
        new("1.5.0", "plugins/github-issues-1.5.0.zip", null, null, null, null);

    private static void _Downloads(IPluginStoreClient storeClient, Action observe) =>
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                observe();
                return Task.FromResult(new PluginStoreDownloadResult(true, null, _ZipPath));
            });

    // Staged, which is what an update over an existing install is: it re-pins the new hash and skips rediscovery,
    // so the run never reaches PluginBootstrap and never touches the real plugins folder on disk.
    private static void _StagesTheUpdate(IPluginInstaller installer) =>
        installer
            .InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
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

        return new PluginManagerViewModel(
            registrationStore,
            installer,
            new PluginBootstrap(),
            dialogService ?? Substitute.For<ISessionDialogService>(),
            Substitute.For<IPluginStoreConfigStore>(),
            storeClient,
            new Dictionary<string, PluginSettingsRegistration>(),
            new PluginDiagnostics(),
            restartService: restartService);
    }

    private static StorePluginRowViewModel _UpdatableRow(string id, string name)
    {
        var version = new PluginStoreVersion("2.0.0", $"plugins/{id}-2.0.0.zip", null, null, null, null);
        var entry = new PluginStoreEntry(id, name, null, "Cockpit", "2.0.0", [version]);

        return new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.example/index.json"), installedVersion: "1.0.0");
    }
}
