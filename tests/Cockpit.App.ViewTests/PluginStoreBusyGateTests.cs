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
    /// The overlay has to be read over everything it reports on. Asserting that its progress bar is visible says
    /// nothing about that — an overlay shrunk to one column, or pushed behind its siblings, leaves the bar
    /// exactly where it was — so this asserts the two things that means: it spans every sibling's box, and it is
    /// drawn after them.
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

            // The sidebar, the catalogue and the detail pane — all three, or the column it misses is not dimmed.
            Assert.Equal(3, covered.Count);
            foreach (var sibling in covered)
            {
                Assert.True(overlay.Bounds.Contains(sibling.Bounds), $"the overlay leaves {sibling.GetType().Name} at {sibling.Bounds} uncovered");
            }

            // Siblings in a Grid paint in declaration order unless ZIndex says otherwise, so both have to hold.
            Assert.Same(content.Children[^1], overlay);
            Assert.DoesNotContain(covered, sibling => sibling.ZIndex > overlay.ZIndex);

            // It paints, and it does not block. AC-420 asserted the opposite here on purpose, when the scrim was
            // the only thing between the operator and a second install. It never was that: a control underneath
            // keeps its focus, so a Tab and a space bar went straight past it, and it stopped only the pointer —
            // leaving the same button dead to the mouse and live to the keyboard. Every route that matters is
            // gated at its own command now (AC-455), so what is left under here is what stays deliberately
            // usable — the settings buttons, the sidebar, the links — and it may not be blocked from the mouse
            // only to look consistent with a scrim.
            Assert.NotNull(overlay.Background);
            Assert.False(overlay.IsHitTestVisible);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The catalogue card's power toggle, rendered — the button AC-455 was reported against. What this adds
    /// over the view-model test is that the card's XAML binds the <em>gated</em> command: a card wired to
    /// something else would leave the button live with every view-model test still green.
    /// </summary>
    [Fact]
    public void ThePowerTogglesOnTheCatalogue_GoDead_WhileTheStoreWorks() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            var manager = _Manager(window);
            window.UpdateLayout();

            var toggles = _ButtonsFor(window, manager.ToggleStorePluginCommand);
            Assert.NotEmpty(toggles);
            Assert.Contains(toggles, button => button.IsEffectivelyEnabled);

            manager.IsBusy = true;
            window.UpdateLayout();

            Assert.DoesNotContain(toggles, button => button.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// And the settings gear beside it does not, which is the decision AC-455 recorded rather than an oversight:
    /// it opens that plugin's own settings and touches nothing an install is working through.
    /// </summary>
    /// <remarks>
    /// This asserts enabled, and enabled only. Whether a <em>pointer</em> reaches it is a different question and
    /// this cannot answer it: the overlay is the gear's sibling rather than its ancestor, so it never touched
    /// <c>IsEffectivelyEnabled</c> — before this change the gear was enabled too and the scrim swallowed every
    /// click on it. The pointer half is pinned one test up, by the overlay's own <c>IsHitTestVisible</c>, which
    /// is the only thing that was ever stopping it. (Hit-testing itself is not available to measure here — it
    /// goes through the compositor, and the headless harness renders no frames.)
    /// </remarks>
    [Fact]
    public void TheSettingsGearBesideIt_StaysEnabled() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            var manager = _Manager(window);
            manager.IsBusy = true;
            window.UpdateLayout();

            var gears = _ButtonsFor(window, manager.OpenStorePluginSettingsCommand);
            Assert.NotEmpty(gears);
            Assert.All(gears, button => Assert.True(button.IsEffectivelyEnabled));
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

    /// <summary>
    /// The search bar sits outside the overlay and used to be switched off wholesale by an IsEnabled binding on
    /// the panel holding it — the pattern AC-456 removed from Update all and AC-455 removed from here. Now every
    /// button on that bar carries its own gate, which has to leave the two controls that change nothing alone.
    /// Rendered, because the bar is at full contrast up there: a live Refresh and a dead one look alike, and
    /// only this can tell them apart.
    /// </summary>
    [Fact]
    public void TheSearchBar_KeepsSearchAndSort_AndLosesRefresh_WhileTheStoreWorks() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("plugin-store");
        try
        {
            var dialog = Assert.IsType<PluginStoreDialogViewModel>(window.DataContext);
            var refresh = Assert.Single(_ButtonsFor(window, dialog.RefreshCommand));
            var search = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), box => box.PlaceholderText == "Search plugins…");
            // The sort picker off the search box's own row, not the version picker in the detail pane.
            var sort = Assert.Single(Assert.IsType<StackPanel>(search.Parent).Children.OfType<ComboBox>());
            window.UpdateLayout();

            Assert.True(refresh.IsEffectivelyEnabled);

            dialog.Manager.IsBusy = true;
            window.UpdateLayout();

            Assert.False(refresh.IsEffectivelyEnabled, "a refresh clears the catalogue the install is walking");
            Assert.True(search.IsEffectivelyEnabled, "searching changes nothing, and reading is what the wait is for");
            Assert.True(sort.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    private static List<Button> _ButtonsFor(Window window, ICommand command) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(button => ReferenceEquals(button.Command, command))];

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
