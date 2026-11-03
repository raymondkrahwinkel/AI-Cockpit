using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Plugins;
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

    /// <summary>
    /// Every button on the catalogue that can start an install, whichever command it goes through — one per
    /// card plus the detail pane's primary action (<c>InstallFromStoreCommand</c>) and its version picker
    /// (<c>InstallSelectedVersionCommand</c>). They reach the same installer and the same folder move, so a
    /// gate on one of them is not a gate. The Installed view's zip install is deliberately not swept here: it
    /// is on the other side of a mutually exclusive view and is not built while the catalogue is showing
    /// (measured — zero of them in this tree), so its gate is held in the view-model tests instead.
    /// </summary>
    [Fact]
    public void EveryInstallButtonOnTheCatalogue_GoesDead_WhileAnInstallRuns() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            var manager = _Manager(window);
            var dialog = Assert.IsType<PluginStoreDialogViewModel>(window.DataContext);
            window.UpdateLayout();

            var starters = new ICommand[]
            {
                manager.InstallFromStoreCommand,
                dialog.InstallSelectedVersionCommand,
            };
            var installButtons = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Command is { } command && starters.Contains(command))
                .ToList();

            // Twelve cards plus the version picker, and at least one of them live — otherwise the assertion
            // below would hold on a screen with nothing to press. The count is asserted so a button that stops
            // binding its command is noticed here rather than quietly dropping out of the sweep.
            Assert.Equal(13, installButtons.Count);
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

    // Only the restart service is load-bearing here — it is what CanRestart's first clause reads. The rest are
    // there because the real constructor asks for them; nothing in this test reaches them.
    private static PluginManagerViewModel _ManagerThatCanRestart() =>
        new(Substitute.For<IPluginAdministration>(),
            Substitute.For<IPluginRegistrationStore>(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IPluginStoreConfigStore>(),
            Substitute.For<IPluginStoreClient>(),
            new Dictionary<string, PluginSettingsRegistration>(),
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
