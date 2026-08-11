using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-704: a live `dotnet-trace` CPU sample caught Cockpit spinning at ~45% CPU, unresponsive, in a feedback loop
/// between <see cref="CockpitViewModel.OnSelectedSessionChanged"/>/<see cref="CockpitViewModel.RefreshPaneVisibility"/>
/// and <see cref="CockpitView.OnSessionPaneGotFocus"/>. In single-pane/zoom layout, switching the selection
/// derealizes the pane just left (`IsPaneVisible` false collapses its container) while it still holds keyboard
/// focus; the framework has to move focus off a control it collapses, and that focus-steal bubbles a `GotFocus`
/// that used to name the just-hidden pane's own session. The pre-existing anti-loop guard only compared
/// <c>ReferenceEquals(SelectedSession, session)</c> — true for the pane that is legitimately gaining focus, but
/// blind to a `GotFocus` naming a pane that is no longer showing at all, which is what a collapse-driven
/// focus-steal always names. That gap is what let the loop close: A hides → focus steals back onto A → the
/// guard waves it through → selection flips to A → B hides → repeat.
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

        // The collapse-driven focus-steal this bug hinges on: a `GotFocus` that bubbles up naming the pane that
        // was just hidden — not a click, not a Tab press, nothing the operator did. `RaiseEvent` bubbles from
        // `inputInA` up its (still-linked) visual parents exactly as a real focus change would, reaching
        // `CockpitView.OnSessionPaneGotFocus` on `SessionGrid`.
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
