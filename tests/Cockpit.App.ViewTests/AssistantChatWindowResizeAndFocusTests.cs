using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-636: the assistant's chat pop-out could not be resized, and it lost the keyboard while the operator was
/// typing in it.
/// </summary>
/// <remarks>
/// Two windows, because that is the only way the focus defect shows: Avalonia keeps one focused element for the
/// whole application. Where the keyboard sits is asserted rather than which window is active — <c>Activate()</c>
/// is a no-op in this backend (see <see cref="SurfaceWindowsTests"/>).
/// </remarks>
[Collection("avalonia")]
public class AssistantChatWindowResizeAndFocusTests
{
    /// <summary>
    /// Criterion 1: this window now sits on the app's own "our title bar, our own resize edges" idiom
    /// (<c>WindowResizeGrip</c>, AC-678), with the existing minimums as the layout's floor.
    /// </summary>
    [Fact]
    public void TheChatWindow_ResizesLikeEveryOtherCockpitWindow() => HeadlessAvalonia.Run(() =>
    {
        var window = new AssistantChatWindow();

        Assert.Equal(WindowDecorations.None, window.WindowDecorations);
        Assert.True(window.CanResize);
        Assert.Equal(340, window.MinWidth);
        Assert.Equal(360, window.MinHeight);
    });

    /// <summary>
    /// Criterion 3 for the pill, which emptied the chat box every time it appeared. On the property, not on where
    /// the keyboard lands: it is what Avalonia hands the platform, and a single attribute an edit could drop.
    /// </summary>
    [Fact]
    public void TheVoicePill_IsShownWithoutTakingTheKeyboard() => HeadlessAvalonia.Run(() =>
        Assert.False(new VoiceOverlayWindow().ShowActivated));

    /// <summary>
    /// Criteria 2 and 3, the reproduction: the view follows every selection change by focusing that pane's input,
    /// which emptied the pop-out's composer mid-sentence. The selection logic itself is left exactly as it was.
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
    /// Criterion 3, the second route to the same steal: in single-pane/zoom mode a <see cref="SessionView"/> focuses
    /// its own composer as it attaches. Guarding only the selection handler would have left this one open.
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
