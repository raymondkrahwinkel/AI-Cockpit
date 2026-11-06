using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-65: the selection has to follow keyboard focus, not only a pointer press, or a terminal can hold focus
/// while the selected session sits on another pane — or on nothing — and the F9 voice hold, text injection and
/// the accent border all miss the pane the operator is in. Both the click handler and the new GotFocus handler
/// find the pane an element belongs to through <see cref="CockpitView._PaneContainerFromSource"/>: the walk up
/// to the child sitting directly in the tile panel. These pin that walk, since it is the piece both paths lean on
/// and the one a visual-tree change would break silently.
/// </summary>
[Collection("avalonia")]
public class PaneFocusSelectionTests
{
    /// <summary>
    /// Reported by Raymond (2026-08-01): switching the active session to an SDK pane moved the selection but
    /// left keyboard focus on the session just left, so the next keystroke went to the wrong session. The helper
    /// both focus paths share only ever looked for a <c>TerminalControl</c>, which a chat pane does not have.
    /// <para>
    /// Two panes, deliberately: a <see cref="SessionView"/> focuses its own composer when it attaches, so a
    /// single-pane test would pass with the defect still in place. Here the second pane's attach is what holds
    /// focus at the start, and only the helper can move it back to the first.
    /// </para>
    /// </summary>
    [Fact]
    public void SelectingAnSdkPane_MovesFocusToItsComposer_NotJustATerminals() => HeadlessAvalonia.Run(() =>
    {
        var paneA = new Border { DataContext = new SessionViewModel(), Child = new SessionView { DataContext = new SessionViewModel() } };
        var paneB = new Border { DataContext = new SessionViewModel(), Child = new SessionView { DataContext = new SessionViewModel() } };

        var panel = new SessionTilePanel();
        panel.Children.Add(paneA);
        panel.Children.Add(paneB);

        var window = new Window { Content = panel, Width = 900, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var composerA = paneA.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "InputBox");
        var composerB = paneB.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "InputBox");

        // Whichever pane attached last is holding focus; pin that, so the assertion below is about the move.
        composerB.Focus();
        Dispatcher.UIThread.RunJobs();
        var startedOnB = composerB.IsFocused;

        CockpitView._FocusInputIn(paneA);
        Dispatcher.UIThread.RunJobs();

        // Read before closing: closing the window drops focus, which would make this pass for the wrong reason
        // in one direction and fail for the wrong reason in the other.
        var movedToA = composerA.IsFocused;
        window.Close();

        Assert.True(startedOnB, "the test needs focus to start on the other pane, or it proves nothing");
        Assert.True(movedToA, "selecting an SDK pane must put the caret in its own composer");
        _SessionMcpDialogKeyboard();
    });

    // F: a nested ItemsControl must not hide the session MCP actions from keyboard traversal (AC-2133).
    private static void _SessionMcpDialogKeyboard()
    {
        var removed = false;
        var result = new SessionMcpSetResult(true, "Assistant", [new SessionMcpServerState("Depot", "connected")], null);
        var viewModel = new SessionMcpDialogViewModel(
            "Session",
            _ => Task.FromResult(result),
            (_, _) =>
            {
                removed = true;
                return Task.FromResult(result);
            });
        viewModel.LoadAsync().GetAwaiter().GetResult();
        var window = new SessionMcpDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        try
        {
            var remove = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Remove"));
            var close = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Close"));

            Assert.True(remove.IsFocused);
            remove.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            Assert.True(close.IsFocused);
            close.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Shift });
            Assert.True(remove.IsFocused);
            remove.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Assert.True(removed);

            close.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
