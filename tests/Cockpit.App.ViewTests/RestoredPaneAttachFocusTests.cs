using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-650: <c>RestoreSessionPanesAsync</c> attaches every restored pane's view in the same startup burst.
/// <see cref="SessionView.OnAttachedToVisualTree"/> used to call <c>InputBox.Focus()</c> unconditionally, so
/// with three or more panes attaching together their focus claims raced each other through
/// <see cref="CockpitView"/>'s selection-driven path and never settled — measured via a live thread dump: the UI
/// thread never blocked, it spun forever inside Avalonia's own TextBox focus teardown
/// (<c>TextBoxTextInputMethodClient.SetPresenter</c>), whose event-subscription list grew past 100k entries
/// during the hang because every hand-off added to it without ever clearing the last one. Pins the guard
/// directly on the call the fix touches, rather than a full multi-pane startup.
/// </summary>
[Collection("avalonia")]
public class RestoredPaneAttachFocusTests
{
    [Fact]
    public void AttachingAnUnselectedPane_DoesNotClaimFocus() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { IsSelected = false };
        var pane = new SessionView { DataContext = session };

        var window = new Window { Content = pane, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var composer = pane.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "InputBox");
        var focused = composer.IsFocused;
        window.Close();

        Assert.False(focused, "a restored pane that is not the selection must not steal the keyboard on attach");
    });

    [Fact]
    public void AttachingTheSelectedPane_StillClaimsFocus() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { IsSelected = true };
        var pane = new SessionView { DataContext = session };

        var window = new Window { Content = pane, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var composer = pane.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "InputBox");
        var focused = composer.IsFocused;
        window.Close();

        Assert.True(focused, "the selected pane must still be ready to type in without a click (L10)");
    });
}
