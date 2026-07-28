using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
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
    [Fact]
    public void TheInstallCommand_IsClosed_WhileSomethingIsAlreadyInstalling()
    {
        var manager = new PluginManagerViewModel();
        var row = _UpdatableRow("github-issues", "GitHub Issues");

        Assert.True(manager.InstallFromStoreCommand.CanExecute(row));

        manager.IsBusy = true;

        // The row itself is unchanged — it is still installable — so the button's IsEnabled binding, which
        // reads only the row, says yes here. What closes it is the command, and only because the work in
        // flight might be some other command's.
        Assert.True(row.CanTakePrimaryAction);
        Assert.False(manager.InstallFromStoreCommand.CanExecute(row));
    }

    /// <summary>
    /// The store is deliberately *not* held while the operator browses for a file (AC-456) — a busy overlay over
    /// a dialog that is waiting on a file picker covers nothing that is working. This pins that choice, and is
    /// the test the previous three rounds did not have: they all measured the window after the scope is entered,
    /// so moving the scope to before the picker changed nothing they could see.
    /// </summary>
    [Fact]
    public async Task TheStoreIsNotHeld_WhileTheFilePickerIsStillOpen()
    {
        var installer = Substitute.For<IPluginInstaller>();
        _StagesTheUpdate(installer);
        var dialogService = Substitute.For<ISessionDialogService>();
        var picker = new TaskCompletionSource<string?>();
        dialogService.PickPluginZipAsync().Returns(_ => picker.Task);
        var manager = _Manager(Substitute.For<IPluginStoreClient>(), installer, Substitute.For<IAppRestartService>(), dialogService);

        var install = manager.InstallFromZipCommand.ExecuteAsync(null);

        Assert.False(manager.IsBusy);

        picker.SetResult(_ZipPath);
        await install;

        Assert.False(manager.IsBusy);
    }

    /// <summary>
    /// The measured repro from AC-456: press Install from zip, and while the picker is parked start an install
    /// from the catalogue. Both routes reach the same installer unpacking into the same folder, and the zip
    /// route used to raise its count blindly on the way back out — two installers at once. It asks for the store
    /// now instead of assuming it, so the one that comes back late does not install on top of the other.
    /// </summary>
    [Fact]
    public async Task AZipInstall_ThatComesBackToAClaimedStore_DoesNotInstallOnTopOfIt()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        _StagesTheUpdate(installer);
        var dialogService = Substitute.For<ISessionDialogService>();
        var picker = new TaskCompletionSource<string?>();
        dialogService.PickPluginZipAsync().Returns(_ => picker.Task);
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>(), dialogService);

        // Held open so the catalogue install is genuinely still running when the picker returns, as it is in the repro.
        var download = new TaskCompletionSource<PluginStoreDownloadResult>();
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => download.Task);

        var zipInstall = manager.InstallFromZipCommand.ExecuteAsync(null);

        // The premise of the repro, and it survives this fix: with the picker open the store reports itself idle,
        // so the catalogue's install button is live. What changes is what happens when the two meet.
        var row = _UpdatableRow("github-issues", "GitHub Issues");
        Assert.True(manager.InstallFromStoreCommand.CanExecute(row));
        var storeInstall = manager.InstallFromStoreCommand.ExecuteAsync(row);

        picker.SetResult(_ZipPath);
        await zipInstall;

        download.SetResult(new PluginStoreDownloadResult(true, null, _ZipPath));
        await storeInstall;

        await installer.Received(1).InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.False(manager.IsBusy);
    }

    /// <summary>
    /// The claim on the folder is handed back when the install ends, or the store takes one install per session
    /// and silently refuses every one after it. Nothing else here observes that release — every other test does
    /// a single install — so deleting the reset left the whole suite green.
    /// </summary>
    [Fact]
    public async Task TheStore_TakesASecondInstall_OnceTheFirstIsDone()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        _Downloads(storeClient, () => { });
        _StagesTheUpdate(installer);
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>());
        var row = _UpdatableRow("github-issues", "GitHub Issues");

        await manager.InstallFromStoreCommand.ExecuteAsync(row);
        await manager.InstallFromStoreCommand.ExecuteAsync(row);

        await installer.Received(2).InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.False(manager.IsBusy);
    }

    /// <summary>
    /// The per-version install — the detail panel's rollback — holds the folder like the rest. Its button is
    /// gated by the dialog's own CanInstallSelectedVersion, so taking it back out of the shared claim broke
    /// nothing any test could see until this one.
    /// </summary>
    [Fact]
    public async Task AVersionInstall_HoldsTheFolder_AgainstAZipReturningFromItsPicker()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        _StagesTheUpdate(installer);
        var dialogService = Substitute.For<ISessionDialogService>();
        var picker = new TaskCompletionSource<string?>();
        dialogService.PickPluginZipAsync().Returns(_ => picker.Task);
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>(), dialogService);
        var download = new TaskCompletionSource<PluginStoreDownloadResult>();
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => download.Task);

        var zipInstall = manager.InstallFromZipCommand.ExecuteAsync(null);
        var row = _UpdatableRow("github-issues", "GitHub Issues");
        var versionInstall = manager.InstallStoreVersionAsync(row, _RollbackVersion);

        picker.SetResult(_ZipPath);
        await zipInstall;

        download.SetResult(new PluginStoreDownloadResult(true, null, _ZipPath));
        await versionInstall;

        await installer.Received(1).InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// And so does a running batch, which is the longest any of them holds it — ten plugins' worth of window
    /// for a zip install to come back into.
    /// </summary>
    [Fact]
    public async Task ARunningBatch_HoldsTheFolder_AgainstAZipReturningFromItsPicker()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        _StagesTheUpdate(installer);
        var dialogService = Substitute.For<ISessionDialogService>();
        var picker = new TaskCompletionSource<string?>();
        dialogService.PickPluginZipAsync().Returns(_ => picker.Task);
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>(), dialogService);
        manager.AvailablePlugins.Add(_UpdatableRow("github-issues", "GitHub Issues"));
        var download = new TaskCompletionSource<PluginStoreDownloadResult>();
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => download.Task);

        var zipInstall = manager.InstallFromZipCommand.ExecuteAsync(null);
        var batch = manager.UpdateAllCommand.ExecuteAsync(null);

        picker.SetResult(_ZipPath);
        await zipInstall;

        download.SetResult(new PluginStoreDownloadResult(true, null, _ZipPath));
        await batch;

        await installer.Received(1).InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The batch update is the fourth route into the folder, and its gate belongs on its command like the other
    /// three. It used to hang off an IsEnabled binding on the whole search bar — so it was held shut by a
    /// control it has nothing to do with, and rearranging that bar would have dropped it onto the backstop.
    /// </summary>
    [Fact]
    public void TheBatchUpdate_IsClosed_WhileSomethingIsAlreadyInstalling()
    {
        var manager = new PluginManagerViewModel();
        var reasked = 0;
        manager.UpdateAllCommand.CanExecuteChanged += (_, _) => reasked++;

        Assert.True(manager.UpdateAllCommand.CanExecute(null));

        manager.IsBusy = true;

        Assert.False(manager.UpdateAllCommand.CanExecute(null));
        Assert.NotEqual(0, reasked);
    }

    /// <summary>
    /// The zip install holds the store while it runs, so it is not a way in behind the other gates. It used to
    /// raise nothing at all: no overlay, and every other install route still open on top of it — the gate on it
    /// only closed the other direction. Scoped to what this measures: the window *after* the file picker.
    /// </summary>
    [Fact]
    public async Task ARunningZipInstall_OnceItIsPastThePicker_HoldsTheStore()
    {
        var installer = Substitute.For<IPluginInstaller>();
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.PickPluginZipAsync().Returns(_ => Task.FromResult<string?>(_ZipPath));
        var manager = _Manager(Substitute.For<IPluginStoreClient>(), installer, Substitute.For<IAppRestartService>(), dialogService);

        var duringTheZipInstall = new List<(bool Busy, bool StoreInstallOpen, bool ZipOpen, bool RestartOnOffer)>();
        installer.InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                duringTheZipInstall.Add((
                    manager.IsBusy,
                    manager.InstallFromStoreCommand.CanExecute(_UpdatableRow("github-issues", "GitHub Issues")),
                    manager.InstallFromZipCommand.CanExecute(null),
                    manager.RestartNowCommand.CanExecute(null)));

                return Task.FromResult(PluginInstallResult.Success("plugin-folder", "sha", staged: true));
            });

        await manager.InstallFromZipCommand.ExecuteAsync(null);

        Assert.Equal([(true, false, false, false)], duringTheZipInstall);
        Assert.False(manager.IsBusy);
    }

    /// <summary>
    /// A nested operation may not report the store idle while an outer one is still running. Every install path
    /// ends by re-browsing the catalogue, and browsing raises the busy signal itself — so with a plain flag the
    /// browse's own exit cleared it mid-install and re-opened every gate that reads it, the restart included.
    /// </summary>
    [Fact]
    public async Task ANestedBrowse_DoesNotReportTheStoreIdle_WhileAnInstallIsStillRunning()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>());
        manager.Stores.Add(PluginStoreConfig.Remote("https://store.example/index.json"));
        storeClient.FetchIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new PluginStoreFetchResult(false, "unreachable", null, null)));

        // Observed from inside the install, after a full browse has come and gone underneath it.
        var whileInstalling = new List<(bool Busy, bool RestartOnOffer)>();
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await manager.BrowseStoresCommand.ExecuteAsync(null);
                whileInstalling.Add((manager.IsBusy, manager.RestartNowCommand.CanExecute(null)));

                return new PluginStoreDownloadResult(true, null, _ZipPath);
            });
        _StagesTheUpdate(installer);

        await manager.InstallFromStoreCommand.ExecuteAsync(_UpdatableRow("github-issues", "GitHub Issues"));

        Assert.Equal([(true, false)], whileInstalling);
        Assert.False(manager.IsBusy, "and it does come down once the outermost operation is done");
    }

    /// <summary>
    /// The zip install reaches the same installer as a store install, so it waits its turn too. It lives on the
    /// Installed view, which is not built while the catalogue is showing, so the rendered sweep cannot see it —
    /// this is where its gate is held.
    /// </summary>
    [Fact]
    public void TheZipInstall_IsClosed_WhileSomethingIsAlreadyInstalling()
    {
        var manager = new PluginManagerViewModel();

        Assert.True(manager.InstallFromZipCommand.CanExecute(null));

        manager.IsBusy = true;

        Assert.False(manager.InstallFromZipCommand.CanExecute(null));
    }

    /// <summary>
    /// A command's CanExecute is only half of a dead button: a bound Avalonia button caches the answer and is
    /// told to ask again by CanExecuteChanged. Without that, the gate above is true and the button still works.
    /// </summary>
    [Fact]
    public void EveryGatedCommand_IsToldToReassess_WhenBusyFlips()
    {
        var manager = new PluginManagerViewModel();
        var reasked = new Dictionary<string, int> { ["install"] = 0, ["restart"] = 0, ["zip"] = 0 };
        manager.InstallFromStoreCommand.CanExecuteChanged += (_, _) => reasked["install"]++;
        manager.RestartNowCommand.CanExecuteChanged += (_, _) => reasked["restart"]++;
        manager.InstallFromZipCommand.CanExecuteChanged += (_, _) => reasked["zip"]++;

        manager.IsBusy = true;

        Assert.DoesNotContain(reasked, entry => entry.Value == 0);
    }

    /// <summary>
    /// The reported failure, driven end to end: ten pending updates, and the restart offer appearing after the
    /// first one. This asserts the offer is unreachable at every step the batch is still running — and that the
    /// batch really does reach the state that made it reachable, so the test cannot pass by the flag never
    /// flipping at all.
    /// </summary>
    [Fact]
    public async Task ARunningBatch_KeepsTheRestartOfferOutOfReach_UntilItIsDone()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>());
        foreach (var row in _UpdatableRows("github-issues", "git-status", "workflows"))
        {
            manager.AvailablePlugins.Add(row);
        }

        var midBatch = new List<(bool NeedsRestart, bool RestartOnOffer)>();
        _Downloads(storeClient, () => midBatch.Add((manager.NeedsRestart, manager.RestartNowCommand.CanExecute(null))));
        _StagesTheUpdate(installer);

        await manager.UpdateAllCommand.ExecuteAsync(null);

        Assert.Equal(3, midBatch.Count);
        Assert.Contains(midBatch, step => step.NeedsRestart);
        Assert.DoesNotContain(midBatch, step => step.RestartOnOffer);
        // And it comes back once the batch is over, or the operator can never apply what they just installed.
        Assert.True(manager.NeedsRestart);
        Assert.True(manager.RestartNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARunningBatch_CountsItsPluginsForTheOverlaysBar()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>());
        foreach (var row in _UpdatableRows("github-issues", "git-status", "workflows"))
        {
            manager.AvailablePlugins.Add(row);
        }

        var progress = new List<(long Percent, bool Indeterminate)>();
        _Downloads(storeClient, () => progress.Add(((long)Math.Round(manager.BusyProgressValue), manager.BusyProgressIndeterminate)));
        _StagesTheUpdate(installer);

        await manager.UpdateAllCommand.ExecuteAsync(null);

        Assert.Equal([(0L, false), (33L, false), (67L, false)], progress);
        // Back to indeterminate afterwards: the next single install has no fraction to draw, and a bar left at
        // 100% would be showing the previous job's progress behind it.
        Assert.False(manager.IsBusy);
        Assert.True(manager.BusyProgressIndeterminate);
        Assert.Equal(0, manager.BusyProgressValue);
    }

    /// <summary>
    /// One plugin failing does not abort the batch — it is caught and the loop carries on — so the overlay has
    /// to come down on that path too, or a batch with a single bad plugin ends with the dialog behind a cover.
    /// </summary>
    [Fact]
    public async Task ABatchThatLosesAPlugin_StillClearsTheOverlay()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>());
        foreach (var row in _UpdatableRows("github-issues", "git-status"))
        {
            manager.AvailablePlugins.Add(row);
        }

        var attempt = 0;
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++attempt == 1
                ? throw new IOException("the store went away mid-download")
                : Task.FromResult(new PluginStoreDownloadResult(true, null, _ZipPath)));
        _StagesTheUpdate(installer);

        await manager.UpdateAllCommand.ExecuteAsync(null);

        Assert.Equal(2, attempt);
        Assert.False(manager.IsBusy);
        Assert.True(manager.BusyProgressIndeterminate);
        Assert.True(manager.RestartNowCommand.CanExecute(null));
    }

    // A single install has one step, so there is no honest fraction to draw and the overlay's bar runs
    // indeterminate. The byte count that would give a real one does not exist: the store client buffers the
    // whole response body before returning (PluginStoreClient._ReadBytesAsync), so nothing reports partway.
    [Fact]
    public async Task ASingleInstall_LeavesTheBarWithoutAFraction()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var installer = Substitute.For<IPluginInstaller>();
        var manager = _Manager(storeClient, installer, Substitute.For<IAppRestartService>());
        var row = _UpdatableRow("github-issues", "GitHub Issues");

        var midInstall = new List<(bool Busy, bool Indeterminate)>();
        _Downloads(storeClient, () => midInstall.Add((manager.IsBusy, manager.BusyProgressIndeterminate)));
        _StagesTheUpdate(installer);

        await manager.InstallFromStoreCommand.ExecuteAsync(row);

        Assert.Equal([(true, true)], midInstall);
        Assert.False(manager.IsBusy);
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
            .InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(PluginInstallResult.Success("plugin-folder", "sha256-of-the-new-bytes", staged: true)));

    private static PluginManagerViewModel _Manager(
        IPluginStoreClient storeClient,
        IPluginInstaller installer,
        IAppRestartService restartService,
        ISessionDialogService? dialogService = null)
    {
        var registrationStore = Substitute.For<IPluginRegistrationStore>();
        registrationStore
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(new Dictionary<string, PluginRegistration>()));

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

    private static IEnumerable<StorePluginRowViewModel> _UpdatableRows(params string[] ids) =>
        ids.Select(id => _UpdatableRow(id, id));

    private static StorePluginRowViewModel _UpdatableRow(string id, string name)
    {
        var version = new PluginStoreVersion("2.0.0", $"plugins/{id}-2.0.0.zip", null, null, null, null);
        var entry = new PluginStoreEntry(id, name, null, "Cockpit", "2.0.0", [version]);

        return new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.example/index.json"), installedVersion: "1.0.0");
    }
}
