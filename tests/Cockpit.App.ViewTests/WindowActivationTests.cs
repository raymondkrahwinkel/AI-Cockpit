using Avalonia.Controls;
using Cockpit.App.Services;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-765: a second click on the assistant button opened the chat window via <c>Show()</c> + <c>Activate()</c>,
/// but <c>Show()</c> on an already-visible window is a no-op — so a minimized window stayed minimized.
/// <see cref="WindowActivation.BringToFront"/> is the fix shared by every "surface this window again" click
/// path (the assistant chat pop-out and the tray's "Show Cockpit").
/// </summary>
[Collection("avalonia")]
public class WindowActivationTests
{
    [Fact]
    public void BringToFront_OnAMinimizedWindow_RestoresItToNormal() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        window.WindowState = WindowState.Minimized;

        WindowActivation.BringToFront(window);

        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(window.IsVisible);

        window.Close();
    });

    // BringToFront_OnAnAlreadyNormalWindow_LeavesItNormalAndVisible stood here: a shown window, still Normal and
    // visible afterwards. A "restore and activate" helper can only fail that by minimising or hiding the window it
    // was asked to surface, and no realistic change to those three lines does that with the case above still green.
}
