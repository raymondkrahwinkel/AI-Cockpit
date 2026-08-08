using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-636, the two defects Raymond hit in live use on the assistant's chat pop-out: it could not be resized, and
/// the assistant closing a session took the keyboard out of it while he was typing there.
/// </summary>
/// <remarks>
/// The focus tests are here rather than in the unit tests because the defect is only visible with two real windows
/// up: Avalonia keeps one application-wide focused element for every window there is (see
/// <see cref="AutoFocus"/>), which is exactly why a pane focusing its own composer could reach across into the
/// pop-out. Window *activation* is deliberately not what they assert — <c>Activate()</c> is a no-op in this
/// backend and <c>IsActive</c> never becomes true (recorded in <see cref="SurfaceWindowsTests"/>), so a test built
/// on it would pass either way. Where the keyboard is *is* measurable, and it is also the thing the operator
/// feels: the caret leaving the box they were typing in.
/// <para>
/// The other half of the bargain — a selection change inside the main window still moving focus to the newly
/// selected pane, criterion 4 — is pinned by
/// <see cref="PaneFocusSelectionTests.SelectingAnSdkPane_MovesFocusToItsComposer_NotJustATerminals"/>, which runs
/// the same helper with both panes in one window.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class AssistantChatWindowResizeAndFocusTests
{
    /// <summary>
    /// Criterion 1. AC-543 fixed this window at 420x560 with no OS chrome at all, so there was nothing to drag;
    /// AC-636 reverses that. `BorderOnly` is the app's own idiom for "our title bar, the OS's resize border"
    /// (<c>CockpitWindowChrome.Apply</c>), so what is asserted is that this window now sits on it too — with the
    /// existing minimums still holding the layout's floor.
    /// </summary>
    [Fact]
    public void TheChatWindow_ResizesLikeEveryOtherCockpitWindow() => HeadlessAvalonia.Run(() =>
    {
        var window = new AssistantChatWindow();

        Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
        Assert.True(window.CanResize);
        Assert.Equal(340, window.MinWidth);
        Assert.Equal(360, window.MinHeight);
    });

    /// <summary>
    /// Criteria 2 and 3, the reproduction. The assistant closing a session moves <c>SelectedSession</c> to the next
    /// pane (left exactly as it was), and the view follows every selection change by focusing that pane's input —
    /// which, focus being application-wide, emptied the pop-out's composer mid-sentence.
    /// </summary>
    [Fact]
    public void FocusingAPanesInput_WhileTheKeyboardIsInAnotherWindow_LeavesItThere() => HeadlessAvalonia.Run(() =>
    {
        var (main, pane) = _ShownPane();
        var (popout, popoutBox) = _ShownPopout();

        popoutBox.Focus();
        Dispatcher.UIThread.RunJobs();
        var startedInThePopout = popoutBox.IsFocused;

        // What CloseSessionAsync's reselection reaches, through CockpitView's SelectedSession handler.
        CockpitView._FocusInputIn(pane);
        Dispatcher.UIThread.RunJobs();

        // Read before closing: closing a window drops focus, which would make this pass for the wrong reason.
        var stayedInThePopout = popoutBox.IsFocused;
        var stolenByThePane = _ComposerIn(pane).IsFocused;
        popout.Close();
        main.Close();

        Assert.True(startedInThePopout, "the test needs the keyboard to start in the pop-out, or it proves nothing");
        Assert.False(stolenByThePane, "a pane must not take the keyboard out of the window the operator is in");
        Assert.True(stayedInThePopout);
    });

    /// <summary>
    /// Criterion 3, the second route to the same steal: in single-pane/zoom mode the reselection swaps which pane is
    /// realised, and a <see cref="SessionView"/> focuses its own composer as it attaches. Guarding only the
    /// selection handler would have left this one open and the pop-out still losing the caret.
    /// </summary>
    [Fact]
    public void APaneAppearing_WhileTheKeyboardIsInAnotherWindow_LeavesItThere() => HeadlessAvalonia.Run(() =>
    {
        var main = new Window { Content = new Decorator(), Width = 800, Height = 600 };
        main.Show();
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var (popout, popoutBox) = _ShownPopout();
        popoutBox.Focus();
        Dispatcher.UIThread.RunJobs();

        var pane = _Pane();
        ((Decorator)main.Content!).Child = pane;
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var stayedInThePopout = popoutBox.IsFocused;
        var stolenByThePane = _ComposerIn(pane).IsFocused;
        popout.Close();
        main.Close();

        Assert.False(stolenByThePane, "a pane appearing must not take the keyboard out of the pop-out");
        Assert.True(stayedInThePopout);
    });

    private static Border _Pane() =>
        new() { DataContext = new SessionViewModel(), Child = new SessionView { DataContext = new SessionViewModel() } };

    private static (Window Window, Border Pane) _ShownPane()
    {
        var pane = _Pane();
        var window = new Window { Content = pane, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return (window, pane);
    }

    /// <summary>Stands in for the chat pop-out: another window with something the operator is typing in.</summary>
    private static (Window Window, TextBox Box) _ShownPopout()
    {
        var box = new TextBox();
        var window = new Window { Content = box, Width = 420, Height = 560 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return (window, box);
    }

    private static TextBox _ComposerIn(Control pane) =>
        pane.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "InputBox");
}
