using System.Windows.Input;
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

    /// <summary>
    /// The overlay has to actually cover what it is drawn over. Asserting that its progress bar is visible says
    /// nothing about that — an overlay shrunk to one column, or pushed behind its siblings, leaves the bar
    /// exactly where it was — so this asserts the two things "covers" means: it spans every sibling's box, and
    /// it is drawn after them.
    /// </summary>
    [Fact]
    public void TheBusyOverlay_SpansItsSiblings_AndIsDrawnOverThem() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            _Manager(window).IsBusy = true;
            window.UpdateLayout();

            var overlay = window.GetControl<Border>("BusyOverlay");
            var content = Assert.IsType<Grid>(overlay.Parent);
            var covered = content.Children.Where(child => !ReferenceEquals(child, overlay) && child.IsVisible).ToList();

            // The sidebar, the catalogue and the detail pane — all three, or the column it misses stays live.
            Assert.Equal(3, covered.Count);
            foreach (var sibling in covered)
            {
                Assert.True(overlay.Bounds.Contains(sibling.Bounds), $"the overlay leaves {sibling.GetType().Name} at {sibling.Bounds} uncovered");
            }

            // Siblings in a Grid paint in declaration order unless ZIndex says otherwise, so both have to hold.
            Assert.Same(content.Children[^1], overlay);
            Assert.DoesNotContain(covered, sibling => sibling.ZIndex > overlay.ZIndex);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The version picker's Install sits in the same pane and reaches the same download-and-move, so it needs
    /// the same gate. The overlay is not that gate: it covers the button against a pointer, but a control
    /// underneath it keeps its focus, so Tab and a space bar still reach it.
    /// </summary>
    [Fact]
    public void TheVersionPickersInstall_GoesDead_Too_AndIsNotMerelyCovered() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            var manager = _Manager(window);
            var dialog = Assert.IsType<PluginStoreDialogViewModel>(window.DataContext);
            window.UpdateLayout();

            var install = Assert.Single(
                window.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button.Command, dialog.InstallSelectedVersionCommand));
            Assert.True(install.IsEffectivelyEnabled);

            manager.IsBusy = true;
            window.UpdateLayout();

            Assert.False(install.IsEffectivelyEnabled, "a second install of the same plugin must not be startable");
            Assert.False(dialog.InstallSelectedVersionCommand.CanExecute(null));
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
