using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The store dialog while it works (AC-420), rendered. A command's CanExecute and a button's enabled state are
/// two different things — the button caches the answer and is told to ask again — so a gate that reads correct
/// in the view model can still leave a live button on the screen. This drives the dialog into the busy state
/// after it is up, which is how a batch actually starts: with the dialog already open in front of the operator.
/// </summary>
[Collection("avalonia")]
public class PluginStoreBusyGateTests
{
    [Fact]
    public void TheRestartOffer_GoesDead_WhenABatchStarts() => HeadlessAvalonia.Run(() =>
    {
        // Built here rather than taken from the screenshot scene: that one carries the design-time view model,
        // which has no restart service, so its restart button is already dead for a reason that has nothing to
        // do with this gate — and a test that cannot see the button live proves nothing about it going dead.
        var manager = _ManagerThatCanRestart();
        var window = new PluginStoreDialog { DataContext = new PluginStoreDialogViewModel(manager) };
        window.Show();
        try
        {
            // "Update all" raises this after the *first* plugin of the batch, which is the whole defect: the
            // offer appears while the rest are still downloading.
            manager.NeedsRestart = true;
            window.UpdateLayout();

            var restart = _RestartButton(window);
            Assert.True(restart.IsEffectivelyEnabled, "an idle store with a staged change offers the restart");

            manager.IsBusy = true;
            window.UpdateLayout();

            Assert.True(restart.IsEffectivelyVisible, "the offer stays on screen — it is out of reach, not gone");
            Assert.False(restart.IsEffectivelyEnabled, "restarting mid-batch abandons the plugins still to come");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void EveryInstallButton_GoesDead_WhileAnInstallRuns() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            var manager = _Manager(window);
            window.UpdateLayout();

            var installButtons = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, manager.InstallFromStoreCommand))
                .ToList();

            // The detail pane's button plus one per catalogue card, and at least one of them live — otherwise
            // the assertion below would hold on a screen with nothing to press.
            Assert.Contains(installButtons, button => button.IsEffectivelyEnabled);

            manager.IsBusy = true;
            window.UpdateLayout();

            Assert.DoesNotContain(installButtons, button => button.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheBusyOverlay_ComesUpOverTheCatalogue_AndGoesAwayAgain() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            var manager = _Manager(window);
            window.UpdateLayout();

            var bar = Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>());
            Assert.False(bar.IsEffectivelyVisible, "an idle store shows no progress");

            manager.StatusMessage = "Downloading 'GitHub Issues' v1.8.0…";
            manager.IsBusy = true;
            window.UpdateLayout();

            Assert.True(bar.IsEffectivelyVisible);
            Assert.True(bar.IsIndeterminate, "a single install has one step and no fraction to draw");

            // The batch case: the same counter the footer's status line is written from.
            manager.BusyProgressIndeterminate = false;
            manager.BusyProgressValue = 200.0 / 6;
            window.UpdateLayout();

            Assert.False(bar.IsIndeterminate);
            Assert.Equal(33, Math.Round(bar.Value));

            manager.IsBusy = false;
            window.UpdateLayout();

            Assert.False(bar.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    // Only the restart service is load-bearing here — it is what CanRestart's first clause reads. The rest are
    // there because the real constructor asks for them; nothing in this test reaches them.
    private static PluginManagerViewModel _ManagerThatCanRestart() =>
        new(Substitute.For<IPluginRegistrationStore>(),
            Substitute.For<IPluginInstaller>(),
            new PluginBootstrap(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IPluginStoreConfigStore>(),
            Substitute.For<IPluginStoreClient>(),
            new Dictionary<string, PluginSettingsRegistration>(),
            new PluginDiagnostics(),
            restartService: Substitute.For<IAppRestartService>());

    private static PluginManagerViewModel _Manager(Window window) =>
        Assert.IsType<PluginStoreDialogViewModel>(window.DataContext).Manager;

    private static Button _RestartButton(Window window)
    {
        var restartCommand = _Manager(window).RestartNowCommand;

        return Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => ReferenceEquals(button.Command, restartCommand));
    }
}
