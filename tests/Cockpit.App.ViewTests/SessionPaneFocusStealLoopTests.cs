using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-704: reproduces the ~45% CPU feedback loop between <see cref="CockpitViewModel.RefreshPaneVisibility"/>
/// and <see cref="CockpitView.OnSessionPaneGotFocus"/> — derealizing the pane a selection just left could
/// steal focus back onto it, and the old ReferenceEquals-only guard let that flip the selection back. Pins
/// that a visible pane's GotFocus still moves the selection (AC-65).
/// </summary>
[Collection("avalonia")]
public class SessionPaneFocusStealLoopTests
{
    [Fact]
    public void AFocusEventFromANoLongerVisiblePane_DoesNotPullTheSelectionBackToIt() => HeadlessAvalonia.Run(() =>
    {
        var sessionA = new SessionViewModel();
        var sessionB = new SessionViewModel();
        var cockpit = new CockpitViewModel { GlobalSingleSessionLayout = true };
        cockpit.Sessions.Add(sessionA);
        cockpit.Sessions.Add(sessionB);

        var view = new CockpitView { DataContext = cockpit };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        window.UpdateLayout();

        cockpit.SelectSessionCommand.Execute(sessionA);
        window.UpdateLayout();

        var grid = view.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "SessionGrid");
        var containerA = grid.GetVisualDescendants().OfType<ContentPresenter>().First(c => ReferenceEquals(c.DataContext, sessionA));
        var inputInA = containerA.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "InputBox");

        // Switching to B is what derealizes A in single-pane mode: `RefreshPaneVisibility` clears A's
        // `IsPaneVisible`, collapsing the container `inputInA` still sits in.
        cockpit.SelectSessionCommand.Execute(sessionB);
        window.UpdateLayout();

        Assert.False(sessionA.IsPaneVisible, "the harness must actually hide A's pane, or this proves nothing");
        Assert.Same(sessionB, cockpit.SelectedSession);

        // AC-704: the collapse-driven focus-steal — a `GotFocus` bubbling up naming the pane just hidden, not
        // anything the operator did. `RaiseEvent` bubbles from `inputInA` up its still-linked visual parents
        // like a real focus change would, reaching `OnSessionPaneGotFocus`.
        inputInA.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent) { Source = inputInA });

        var selected = cockpit.SelectedSession;
        window.Close(); // stops the view's idle-sweep/resource/claim timers, or they keep ticking on the shared test thread
        Assert.Same(sessionB, selected);
    });

    /// <summary>
    /// The guard's original job (AC-65) still has to hold: a real focus change landing on a pane that IS showing
    /// — a click, Tab, or anything else — must still move the selection there.
    /// </summary>
    [Fact]
    public void AFocusEventFromAVisiblePane_StillMovesTheSelectionThere() => HeadlessAvalonia.Run(() =>
    {
        var sessionA = new SessionViewModel();
        var sessionB = new SessionViewModel();
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Add(sessionA);
        cockpit.Sessions.Add(sessionB);

        var view = new CockpitView { DataContext = cockpit };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        window.UpdateLayout();

        cockpit.SelectSessionCommand.Execute(sessionA);
        window.UpdateLayout();

        var grid = view.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "SessionGrid");
        var containerB = grid.GetVisualDescendants().OfType<ContentPresenter>().First(c => ReferenceEquals(c.DataContext, sessionB));
        var inputInB = containerB.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "InputBox");

        Assert.True(sessionB.IsPaneVisible, "grid mode shows every pane, so B must still be visible for this test to mean anything");

        inputInB.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent) { Source = inputInB });

        var selected = cockpit.SelectedSession;
        window.Close();
        Assert.Same(sessionB, selected);
    });
}
