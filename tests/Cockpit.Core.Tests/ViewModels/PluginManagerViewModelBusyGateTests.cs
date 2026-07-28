using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// What the store may not let the operator do while it is working (AC-420). Two affordances stayed live during
/// an install: the detail pane's install button, which binds only to the row's own <c>CanTakePrimaryAction</c>
/// and re-entered into a second download onto the same folder, and "Restart the cockpit now", which "Update all"
/// offers after the *first* plugin of a batch — pressing it there left plugins 2..n silently un-updated behind a
/// banner saying the update was done.
/// </summary>
public class PluginManagerViewModelBusyGateTests
{
    [Fact]
    public void TheInstallCommand_IsClosed_WhileSomethingIsAlreadyInstalling()
    {
        var manager = new PluginManagerViewModel();
        var row = _UpdatableRow("github-issues", "GitHub Issues");

        Assert.True(manager.InstallFromStoreCommand.CanExecute(row));

        manager.IsBusy = true;

        // The row itself is unchanged — it is still installable — so a gate that only reads the row (which is
        // what the button's IsEnabled binding does) says yes here, and a second click starts a second install.
        Assert.True(row.CanTakePrimaryAction);
        Assert.False(manager.InstallFromStoreCommand.CanExecute(row));
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

    private static PluginManagerViewModel _Manager(IPluginStoreClient storeClient, IPluginInstaller installer, IAppRestartService restartService)
    {
        var registrationStore = Substitute.For<IPluginRegistrationStore>();
        registrationStore
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(new Dictionary<string, PluginRegistration>()));

        return new PluginManagerViewModel(
            registrationStore,
            installer,
            new PluginBootstrap(),
            Substitute.For<ISessionDialogService>(),
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
