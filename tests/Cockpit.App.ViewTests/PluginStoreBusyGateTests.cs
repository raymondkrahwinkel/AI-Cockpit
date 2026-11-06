using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

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

    private static PluginManagerViewModel _Manager(Window window) =>
        Assert.IsType<PluginStoreDialogViewModel>(window.DataContext).Manager;
}
